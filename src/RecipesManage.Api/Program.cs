using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RecipesManage.Api;
using RecipesManage.Api.Hubs;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Services;
using RecipesManage.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

EmbeddedPostgresRuntime? embeddedPostgres = null;
if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("PostgreSQL")) &&
    EmbeddedPostgresRuntime.ShouldStart())
{
    embeddedPostgres = await EmbeddedPostgresRuntime.StartAsync();
    var postgresCs = await embeddedPostgres.EnsureDatabaseAsync(EmbeddedPostgresRuntime.DefaultAppDatabase);
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:PostgreSQL"] = postgresCs
    });
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddRecipesManage(builder.Configuration, builder.Environment);
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});
builder.Services.AddOpenApi();
builder.Services.AddCors(o => o.AddPolicy("spa", p =>
    p.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await SchemaBootstrap.ApplyAsync(db);

    // 一次性数据修复：历史上这些 Repair* 挂在每个进程启动上跑，会持续改写业务数据。
    // 现在按 applied_data_fixes 里的键只执行一次，并且对受控配方的自动改动留审计。
    await DataFixRunner.ApplyAsync(db, app.Services.GetRequiredService<ILogger<AppDbContext>>());

    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    var seedOptions = new SeedOptions(
        Demo: bool.TryParse(builder.Configuration["Seed:Demo"], out var demo) ? demo : builder.Environment.IsDevelopment(),
        InitialPassword: builder.Configuration["Seed:AdminPassword"]);
    await DatabaseSeeder.SeedAsync(db, hasher, seedOptions, app.Logger);

    // 为升级前已在跑的批次补写设备租约，并清理终态批次的残留租约。
    await scope.ServiceProvider.GetRequiredService<EquipmentLeaseService>().ReconcileAsync();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors("spa");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ExecutionHub>("/hubs/execution");
if (embeddedPostgres is not null)
{
    var pg = embeddedPostgres;
    app.Lifetime.ApplicationStopping.Register(() => pg.DisposeAsync().AsTask().GetAwaiter().GetResult());
}
app.MapGet("/health", async (AppDbContext db) =>
{
    var provider = RecipesDatabase.HealthName(db.Database);
    var ok = await db.Database.CanConnectAsync();
    return ok
        ? Results.Ok(RecipesDatabase.HealthBody(provider, true))
        : Results.Json(RecipesDatabase.HealthBody(provider, false), statusCode: 503);
}).AllowAnonymous();
app.Run();
