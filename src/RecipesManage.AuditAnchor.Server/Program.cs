using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using RecipesManage.AuditAnchor.Protocol;
using RecipesManage.AuditAnchor.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);

var receiver = builder.Configuration.GetSection("AuditAnchorReceiver");
var token = receiver["ClientToken"];
if (string.IsNullOrWhiteSpace(token) || Encoding.UTF8.GetByteCount(token) < 32 ||
    token.Contains("change-me", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("AuditAnchorReceiver:ClientToken must be a unique secret of at least 32 bytes.");

var thumbprint = receiver["CertificateThumbprint"];
if (string.IsNullOrWhiteSpace(thumbprint))
    throw new InvalidOperationException("AuditAnchorReceiver:CertificateThumbprint is required.");

var signer = CertificateReceiptSigner.Load(thumbprint, DateTimeOffset.UtcNow);
var databasePath = receiver["DatabasePath"];
if (string.IsNullOrWhiteSpace(databasePath)) databasePath = "App_Data/audit-anchor.db";
if (!Path.IsPathRooted(databasePath)) databasePath = Path.Combine(builder.Environment.ContentRootPath, databasePath);

builder.Services.AddSingleton(signer);
builder.Services.AddSingleton(new AuditAnchorLedger(databasePath, signer));
var requireHttps = receiver.GetValue("RequireHttps", true);
var app = builder.Build();
var ledger = app.Services.GetRequiredService<AuditAnchorLedger>();
await ledger.InitializeAsync();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "audit-anchor",
    keyId = signer.KeyId,
    pid = Environment.ProcessId
}));

app.MapPost("/v1/anchors", async (HttpContext context, AuditAnchorLedger store, CancellationToken ct) =>
{
    if (!IsAuthorized(context, token!)) return Results.Unauthorized();
    if (requireHttps && !context.Request.IsHttps)
        return Results.BadRequest(new { code = "HTTPS_REQUIRED", message = "The anchor receiver requires HTTPS." });
    if (context.Request.ContentLength is > 64 * 1024)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    AuditAnchorManifest? manifest;
    try
    {
        manifest = await JsonSerializer.DeserializeAsync<AuditAnchorManifest>(context.Request.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { code = "INVALID_MANIFEST", message = "The audit anchor manifest is not valid JSON." });
    }
    if (manifest is null)
        return Results.BadRequest(new { code = "INVALID_MANIFEST", message = "An audit anchor manifest is required." });

    try
    {
        var receipt = await store.AcceptAsync(manifest, ct);
        return Results.Ok(receipt);
    }
    catch (InvalidDataException e)
    {
        return Results.BadRequest(new { code = "INVALID_MANIFEST", message = e.Message });
    }
    catch (AuditAnchorSnapshotConflictException e)
    {
        return Results.Conflict(new { code = "SNAPSHOT_CONFLICT", message = e.Message });
    }
});

app.MapGet("/v1/anchors/{sourceId}", async (
    HttpContext context, string sourceId, long afterSequence, int take, long? throughSequence,
    AuditAnchorLedger store, CancellationToken ct) =>
{
    if (!IsAuthorized(context, token!)) return Results.Unauthorized();
    if (requireHttps && !context.Request.IsHttps)
        return Results.BadRequest(new { code = "HTTPS_REQUIRED", message = "The anchor receiver requires HTTPS." });
    try
    {
        return Results.Ok(await store.ReadPageAsync(
            sourceId, afterSequence, take == 0 ? 100 : take, throughSequence, ct));
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { code = "INVALID_QUERY", message = "Source ID, sequence, or page size is invalid." });
    }
});

app.Run();

static bool IsAuthorized(HttpContext context, string expectedToken)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
    var supplied = Encoding.UTF8.GetBytes(authorization["Bearer ".Length..].Trim());
    var expected = Encoding.UTF8.GetBytes(expectedToken);
    return supplied.Length == expected.Length &&
           CryptographicOperations.FixedTimeEquals(supplied, expected);
}
