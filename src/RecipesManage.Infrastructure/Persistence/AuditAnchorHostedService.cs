using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using RecipesManage.AuditAnchor.Protocol;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Infrastructure.Persistence;

public sealed record AuditAnchorSettings(
    bool Enabled,
    TimeOnly AtUtc,
    int RetryMinutes,
    string SourceId,
    Uri? Endpoint,
    string? ClientToken,
    string? VerificationCertificatePath)
{
    public static AuditAnchorSettings From(
        IConfiguration config, string contentRoot, bool isDevelopment)
    {
        var section = config.GetSection("AuditAnchor");
        var atText = section["AtUtc"] ?? "02:45";
        if (!TimeOnly.TryParseExact(atText, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var atUtc))
            throw new InvalidOperationException("AuditAnchor:AtUtc must use the UTC HH:mm format.");

        var retryMinutes = section.GetValue("RetryMinutes", 5);
        if (retryMinutes is < 1 or > 120)
            throw new InvalidOperationException("AuditAnchor:RetryMinutes must be between 1 and 120.");

        var sourceId = section["SourceId"]?.Trim() ?? "";
        var endpointText = section["Endpoint"]?.Trim() ?? "";
        var token = section["ClientToken"]?.Trim();
        var certificatePath = section["VerificationCertificatePath"]?.Trim();
        if (section.GetValue("Enabled", false))
        {
            if (!Guid.TryParseExact(sourceId, "D", out _))
                throw new InvalidOperationException("AuditAnchor:SourceId must be a stable GUID for this BRMES installation.");
            if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
                throw new InvalidOperationException("AuditAnchor:Endpoint must be an absolute HTTPS URI.");
            if (endpoint.Scheme != Uri.UriSchemeHttps && !isDevelopment)
                throw new InvalidOperationException("AuditAnchor:Endpoint must use HTTPS outside Development.");
            if (string.IsNullOrWhiteSpace(token) || Encoding.UTF8.GetByteCount(token) < 32 ||
                token.Contains("change-me", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AuditAnchor:ClientToken must be a unique secret of at least 32 bytes.");
            if (string.IsNullOrWhiteSpace(certificatePath))
                throw new InvalidOperationException("AuditAnchor:VerificationCertificatePath is required.");

            certificatePath = Path.IsPathRooted(certificatePath)
                ? certificatePath
                : Path.GetFullPath(Path.Combine(contentRoot, certificatePath));
        }
        return new AuditAnchorSettings(
            section.GetValue("Enabled", false),
            atUtc,
            retryMinutes,
            sourceId,
            Uri.TryCreate(endpointText, UriKind.Absolute, out var parsedEndpoint) ? parsedEndpoint : null,
            token,
            certificatePath);
    }
}

public sealed record AuditAnchorRunStatus(
    bool Enabled,
    string SourceId,
    string? Endpoint,
    string ScheduledAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    long? LastReceiptSequence,
    string? LastReceiptHash,
    string? SignerKeyId,
    string? LastError);

public sealed class AuditAnchorHostedService : BackgroundService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2)
    ];

    private readonly AuditAnchorSettings _settings;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _clients;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditAnchorHostedService> _log;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _statusGate = new();
    private readonly X509Certificate2? _verificationCertificate;
    private readonly RSA? _verificationKey;
    private readonly string? _signerKeyId;
    private DateTimeOffset? _lastAttemptAtUtc;
    private DateTimeOffset? _lastSuccessAtUtc;
    private long? _lastReceiptSequence;
    private string? _lastReceiptHash;
    private string? _lastError;

    public AuditAnchorHostedService(
        AuditAnchorSettings settings,
        IServiceScopeFactory scopes,
        IHttpClientFactory clients,
        TimeProvider timeProvider,
        ILogger<AuditAnchorHostedService> log)
    {
        _settings = settings;
        _scopes = scopes;
        _clients = clients;
        _timeProvider = timeProvider;
        _log = log;
        if (!settings.Enabled) return;

        var pem = File.ReadAllText(settings.VerificationCertificatePath!);
        if (!PemEncoding.TryFind(pem.AsSpan(), out var pemFields) ||
            !pem.AsSpan(pemFields.Label).SequenceEqual("CERTIFICATE"))
            throw new InvalidOperationException("Audit anchor verification file must contain a PEM CERTIFICATE.");
        var certificateDer = Convert.FromBase64String(pem[pemFields.Base64Data]);
        _verificationCertificate = X509CertificateLoader.LoadCertificate(certificateDer);
        _verificationKey = _verificationCertificate.GetRSAPublicKey()
            ?? throw new InvalidOperationException("Audit anchor verification certificate must contain an RSA public key.");
        if (_verificationKey.KeySize < 2048)
            throw new InvalidOperationException("Audit anchor verification RSA key must be at least 2048 bits.");
        _signerKeyId = Convert.ToHexString(
            SHA256.HashData(_verificationCertificate.RawData)).ToLowerInvariant();
    }

    public AuditAnchorRunStatus Status()
    {
        lock (_statusGate)
        {
            return new AuditAnchorRunStatus(
                _settings.Enabled,
                _settings.SourceId,
                _settings.Endpoint?.ToString(),
                _settings.AtUtc.ToString("HH:mm", CultureInfo.InvariantCulture),
                _lastAttemptAtUtc,
                _lastSuccessAtUtc,
                _lastReceiptSequence,
                _lastReceiptHash,
                _signerKeyId,
                _lastError);
        }
    }

    public async Task<AuditAnchorReceipt> RunOnceAsync(CancellationToken ct = default)
    {
        if (!_settings.Enabled)
            throw new InvalidOperationException("External audit anchoring is disabled.");
        await _runGate.WaitAsync(ct);
        try
        {
            lock (_statusGate)
            {
                _lastAttemptAtUtc = _timeProvider.GetUtcNow();
                _lastError = null;
            }

            var manifest = await BuildManifestAsync(ct);
            var receipt = await SubmitWithRetryAsync(manifest, ct);
            if (!string.Equals(receipt.SourceId, manifest.SourceId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.SnapshotId, manifest.SnapshotId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.ManifestSha256, AuditAnchorProtocol.ManifestHash(manifest), StringComparison.OrdinalIgnoreCase) ||
                !AuditAnchorProtocol.VerifyReceipt(receipt, _verificationKey!, _signerKeyId!))
                throw new CryptographicException("The audit anchor receiver returned an invalid signed receipt.");

            lock (_statusGate)
            {
                _lastSuccessAtUtc = _timeProvider.GetUtcNow();
                _lastReceiptSequence = receipt.Sequence;
                _lastReceiptHash = receipt.ReceiptHash;
                _lastError = null;
            }
            _log.LogInformation(
                "外部审计锚定成功：来源 {SourceId}，回执序号 {Sequence}，审计 {AuditCount} 行，签名 {SignatureCount} 行",
                manifest.SourceId, receipt.Sequence,
                manifest.Streams.Single(x => x.Stream == AuditAnchorProtocol.AuditLogsStream).Count,
                manifest.Streams.Single(x => x.Stream == AuditAnchorProtocol.SignatureRecordsStream).Count);
            return receipt;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (_statusGate) _lastError = e.Message.Length <= 512 ? e.Message : e.Message[..512];
            _log.LogError(e, "外部审计锚定失败；SQLite 审计数据未被修改");
            throw;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<AuditAnchorManifest> BuildManifestAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var snapshotAt = _timeProvider.GetUtcNow();

        using var auditTree = new AuditMerkleAccumulator(AuditAnchorProtocol.AuditLogsStream);
        var auditRows = db.AuditLogs.AsNoTracking().OrderBy(x => x.At).ThenBy(x => x.Id).AsAsyncEnumerable();
        await foreach (var row in auditRows.WithCancellation(ct))
            auditTree.Append(AuditAnchorRowEncoding.Encode(row));

        using var signatureTree = new AuditMerkleAccumulator(AuditAnchorProtocol.SignatureRecordsStream);
        var signatureRows = db.SignatureRecords.AsNoTracking()
            .OrderBy(x => x.SignedAt).ThenBy(x => x.Id).AsAsyncEnumerable();
        await foreach (var row in signatureRows.WithCancellation(ct))
            signatureTree.Append(AuditAnchorRowEncoding.Encode(row));

        var watermark = await RecipesDatabase.SafeMigrationWatermarkAsync(db, ct);
        await transaction.CommitAsync(ct);
        return new AuditAnchorManifest(
            AuditAnchorProtocol.CurrentVersion,
            Guid.Parse(_settings.SourceId).ToString("D").ToLowerInvariant(),
            Guid.NewGuid().ToString("D").ToLowerInvariant(),
            snapshotAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            RecipesDatabase.Version,
            watermark?.Applied,
            [
                new AuditAnchorStreamRoot(AuditAnchorProtocol.AuditLogsStream, auditTree.Count, auditTree.FinishHex()),
                new AuditAnchorStreamRoot(AuditAnchorProtocol.SignatureRecordsStream, signatureTree.Count, signatureTree.FinishHex())
            ]);
    }

    private async Task<AuditAnchorReceipt> SubmitWithRetryAsync(AuditAnchorManifest manifest, CancellationToken ct)
    {
        var endpoint = new Uri(_settings.Endpoint!.AbsoluteUri.TrimEnd('/') + "/");
        var client = _clients.CreateClient("AuditAnchor");
        Exception? lastError = null;
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "v1/anchors"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ClientToken);
                request.Content = JsonContent.Create(manifest, options: JsonOptions);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = new HttpRequestException(
                        $"Audit anchor receiver returned HTTP {(int)response.StatusCode}.",
                        null, response.StatusCode);
                    if (!IsRetryable(response.StatusCode) || attempt == RetryDelays.Length) throw error;
                    lastError = error;
                }
                else
                {
                    var receipt = await response.Content.ReadFromJsonAsync<AuditAnchorReceipt>(JsonOptions, ct);
                    return receipt ?? throw new InvalidDataException("Audit anchor receiver returned an empty receipt.");
                }
            }
            catch (HttpRequestException e) when (e.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                                                  (int?)e.StatusCode >= 500)
            {
                if (attempt == RetryDelays.Length) throw;
                lastError = e;
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                if (attempt == RetryDelays.Length) throw new TimeoutException("Audit anchor receiver timed out.", e);
                lastError = e;
            }

            await Task.Delay(RetryDelays[attempt], ct);
        }
        throw new HttpRequestException("Audit anchor receiver did not accept the snapshot.", lastError);
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _log.LogInformation("外部审计锚定已禁用（AuditAnchor:Enabled=false）");
            return;
        }

        _log.LogInformation("外部审计锚定已启用：来源 {SourceId}，每日 {AtUtc} UTC，目标 {Endpoint}",
            _settings.SourceId, _settings.AtUtc.ToString("HH:mm", CultureInfo.InvariantCulture), _settings.Endpoint);
        var retry = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _timeProvider.GetUtcNow();
            var delay = retry
                ? TimeSpan.FromMinutes(_settings.RetryMinutes)
                : NextRunAt(now) - now;
            try
            {
                await Task.Delay(delay <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delay, stoppingToken);
                await RunOnceAsync(stoppingToken);
                retry = false;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                retry = true;
                _log.LogError(e, "外部审计锚定本轮失败，将在 {Minutes} 分钟后重试", _settings.RetryMinutes);
            }
        }
    }

    private DateTimeOffset NextRunAt(DateTimeOffset nowUtc)
    {
        var todayAt = new DateTimeOffset(nowUtc.UtcDateTime.Date.Add(_settings.AtUtc.ToTimeSpan()), TimeSpan.Zero);
        return todayAt > nowUtc ? todayAt : todayAt.AddDays(1);
    }

    public override void Dispose()
    {
        _verificationKey?.Dispose();
        _verificationCertificate?.Dispose();
        _runGate.Dispose();
        base.Dispose();
    }
}
