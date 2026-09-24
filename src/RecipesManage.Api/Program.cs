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

// 作为 Windows 服务运行时才接管服务控制与事件日志：开发机上无条件调用会把 ContentRoot 改成 exe 目录，
// 相对路径的 App_Data 就会落到 bin 里（实测踩过一次"库在哪"的困惑），所以由安装脚本显式打开。
if (Environment.GetEnvironmentVariable("BRMES_WINDOWS_SERVICE") == "1")
    builder.Host.UseWindowsService(options => options.ServiceName = "BRMES");

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

// 单实例互斥必须排在建库、迁移、调度器恢复之前：这些步骤任何一件在两个进程里同时跑，
// 后果都不是"报错"而是静默地写坏数据或双写 PLC。拒绝启动是唯一正确的行为。
using var instanceLock = SingleInstanceLock.TryAcquire(app.Configuration.GetConnectionString("Sqlite"), out var lockSkipped);
if (instanceLock is null && !lockSkipped)
{
    var owner = SingleInstanceLock.DescribeOwner(app.Configuration.GetConnectionString("Sqlite"));
    app.Logger.LogCritical(
        "已有另一个 BRMES 实例正在使用这个数据库（{Owner}）。同一台设备只允许一个引擎驱动四步握手，" +
        "因此本次启动被拒绝。要看界面请直接用浏览器打开已在运行的实例；确认没有活实例后可删除 .db.lock 文件后重试。",
        owner ?? "持有者信息不可读");
    // 75 = EX_TEMPFAIL：看门狗据此知道"不是崩溃，别重试"。
    return SingleInstanceLock.DuplicateInstanceExitCode;
}

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
// 令牌里的角色是签发时的快照（默认 12 小时才过期），这里按库内当前用户复核后才进授权。
app.UseMiddleware<CurrentUserMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ExecutionHub>("/hubs/execution");
app.MapGet("/health", async (AppDbContext db) =>
{
    var ok = await db.Database.CanConnectAsync();
    return ok
        ? Results.Ok(RecipesDatabase.HealthBody(RecipesDatabase.Sqlite, true))
        : Results.Json(RecipesDatabase.HealthBody(RecipesDatabase.Sqlite, false), statusCode: 503);
}).AllowAnonymous();
app.Run();

// 顶层语句里一旦有 `return 75`（单实例被拒），整条 Main 就变成 int 返回，
// 正常路径要显式给出 0，否则编译不过（"并非所有的代码路径都返回一个值"）。
return 0;
