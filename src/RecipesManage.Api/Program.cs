using System.Text.Json.Serialization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RecipesManage.Api;
using RecipesManage.Api.Hubs;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Services;
using RecipesManage.Infrastructure.Persistence;
using RecipesManage.Simulation;

var builder = WebApplication.CreateBuilder(args);

// 日志必须落到文件里：作为 Windows 服务跑起来时没有控制台，console provider 等于什么都没写，
// 而现场能带回给支持的只有这份文件（崩过几次、为什么进 Fault、备份哪天没跑成）。
builder.Logging.AddBrmesFileLogging(builder.Configuration);

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

// 文件日志的第一行就要能回答"哪一版、库在哪、日志在哪、备份去哪"——远程支持电话里问的四件事，
// 别让操作员去翻 exe 属性。
app.Logger.LogInformation(
    "BRMES {Version} 启动：内容根 {ContentRoot}，库 {Database}，备份 {Backups}",
    RecipesDatabase.Version, app.Environment.ContentRootPath,
    RecipesDatabase.ResolveConnectionString(app.Configuration.GetConnectionString("Sqlite")),
    app.Services.GetRequiredService<DatabaseBackup>().DirectoryPath);

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
    // 有 pending 迁移时，这里会先落一份已校验的升级前快照；写不成就不迁移（抛异常停在旧版本）。
    await SchemaBootstrap.ApplyAsync(
        db, scope.ServiceProvider.GetRequiredService<DatabaseBackup>(), app.Logger);

    // 一次性数据修复：历史上这些 Repair* 挂在每个进程启动上跑，会持续改写业务数据。
    // 现在按 applied_data_fixes 里的键只执行一次，并且对受控配方的自动改动留审计。
    await DataFixRunner.ApplyAsync(db, app.Services.GetRequiredService<ILogger<AppDbContext>>());

    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    var seedOptions = new SeedOptions(
        Demo: bool.TryParse(builder.Configuration["Seed:Demo"], out var demo) ? demo : builder.Environment.IsDevelopment(),
        InitialPassword: builder.Configuration["Seed:AdminPassword"]);
    await DatabaseSeeder.SeedAsync(db, hasher, seedOptions, app.Logger);
    if (seedOptions.Demo)
        await SimulationSeed.EnsureLoopbackDevicesAsync(db);

    // 为升级前已在跑的批次补写设备租约，并清理终态批次的残留租约。
    await scope.ServiceProvider.GetRequiredService<EquipmentLeaseService>().ReconcileAsync();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseMiddleware<ExceptionHandlingMiddleware>();
// 界面与 API 同源：出包时前端构建进 wwwroot（frontend/vite.config.ts 的 outDir），由 Kestrel 直接托管，
// 现场只需要一个端口。开发机没有这个目录，静态文件中间件是空操作，前端照旧走 vite dev（5173）。
app.UseDefaultFiles();
// 缓存策略必须按文件分两类，否则"升级了但界面没变"：
//   /assets/* 是构建时带内容哈希的文件名，内容一变名字就变，可以长缓存（immutable）；
//   index.html 是入口，它指向"当前这批哈希文件名"。它一旦被浏览器缓存住，用户升级后拿到的
//   仍是旧入口，并会去请求已经被删掉的旧 /assets/xxx.js（那里只会 404，页面白屏或样式错乱），
//   现场只能靠强刷救——所以入口必须每次回源确认（no-cache 允许 304，不是禁用缓存）。
// 不设 Cache-Control 时浏览器按 Last-Modified 推断一个"启发式新鲜期"，恰好会造成这种旧入口。
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl =
            ctx.Context.Request.Path.StartsWithSegments("/assets")
                ? "public,max-age=31536000,immutable"
                : "no-cache";
    }
});
app.UseCors("spa");
app.UseAuthentication();
// 令牌里的角色是签发时的快照（默认 12 小时才过期），这里按库内当前用户复核后才进授权。
app.UseMiddleware<CurrentUserMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ExecutionHub>("/hubs/execution");
app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
{
    var ok = await db.Database.CanConnectAsync(ct);
    var schema = ok ? await RecipesDatabase.SafeMigrationWatermarkAsync(db, ct) : null;
    return ok
        ? Results.Ok(RecipesDatabase.HealthBody(RecipesDatabase.Sqlite, true, schema))
        : Results.Json(RecipesDatabase.HealthBody(RecipesDatabase.Sqlite, false), statusCode: 503);
}).AllowAnonymous();

// SPA 的 history 路由回退：未匹配的非文件请求回 index.html。但 /api 与 /hubs 下的未知路径保持 404——
// 被 index.html 吞掉的话，客户端拿到的是 HTML，排障时看到的是一页界面而不是"接口不存在"。
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { code = "NOT_FOUND", message = "接口不存在。" });
        return;
    }

    // 带扩展名的请求不是前端路由（缺的 favicon、升级后浏览器仍请求的旧 /assets/xxx.js）：
    // 直接 404。回 index.html 会让浏览器把 HTML 当脚本解析，报的是看不懂的语法错误。
    if (Path.HasExtension(context.Request.Path.Value))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    var index = Path.Combine(webRoot, "index.html");
    if (!File.Exists(index))
    {
        // 开发机没有构建产物：给一句能照做的话，而不是 FileNotFound 的 500。
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("前端未构建：开发环境请运行 frontend 的 npm run dev（http://localhost:5173）。");
        return;
    }

    // SPA 回退返回的也是入口页，缓存口径必须与 UseStaticFiles 那侧一致（见上面的说明）。
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(index);
});
app.Run();

// 顶层语句里一旦有 `return 75`（单实例被拒），整条 Main 就变成 int 返回，
// 正常路径要显式给出 0，否则编译不过（"并非所有的代码路径都返回一个值"）。
return 0;
