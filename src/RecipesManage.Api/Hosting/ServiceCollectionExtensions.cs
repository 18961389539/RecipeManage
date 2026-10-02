using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RecipesManage.Api.Hubs;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Identity;
using RecipesManage.Execution;
using RecipesManage.Infrastructure.Persistence;
using RecipesManage.Infrastructure.Plc;
using RecipesManage.Infrastructure.Records;
using RecipesManage.Simulation;

namespace RecipesManage.Api;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRecipesManage(this IServiceCollection services, IConfiguration config, IWebHostEnvironment env)
    {
        services.AddDbContext<AppDbContext>(options =>
        {
            RecipesDatabase.Apply(options, config.GetConnectionString("Sqlite"));
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        });

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        // 登录失败计数是进程内的（单实例部署），所以必须是 singleton；scoped 会让计数每次请求归零。
        services.AddSingleton<LoginGuard>();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<AuthService>();
        services.AddScoped<RecipeService>();
        services.AddScoped<RecipeQueryService>();
        services.AddScoped<RecipeApprovalService>();
        services.AddScoped<RecipePackageService>();
        services.AddScoped<ApprovalChainService>();
        services.AddScoped<MaterialLotService>();
        services.AddScoped<EsignGuard>();
        services.AddScoped<BatchQueryService>();
        services.AddScoped<BatchService>();
        services.AddScoped<EquipmentService>();
        services.AddScoped<AuditService>();
        services.AddScoped<EquipmentLeaseService>();
        // 仿真在单独的 RecipesManage.Simulation 项目里。是否真的绑端口由库里的设备行决定（PlcLoopbackGate），
        // 所以这里无条件注册不会在生产机上占端口；Seed:Demo=false 时那几条设备行根本不会写入。
        services.AddPlcSimulation();
        services.AddPlcLoopbackSimulators();
        services.AddSingleton<IPlcDriverFactory, PlcDriverFactory>();
        services.AddSingleton<IBatchRecordPdf, BatchRecordPdf>();
        services.AddSingleton<BatchSchedulerHostedService>();
        services.AddSingleton<IBatchScheduler>(sp => sp.GetRequiredService<BatchSchedulerHostedService>());
        services.AddHostedService(sp => sp.GetRequiredService<BatchSchedulerHostedService>());
        services.AddSingleton(BackupSettingsFrom(config));
        services.AddSingleton(sp => new DatabaseBackup(
            sp.GetRequiredService<BackupSettings>(),
            RecipesDatabase.ResolveConnectionString(config.GetConnectionString("Sqlite"))));
        services.AddSingleton(MaintenanceSettingsFrom(config));
        services.AddSingleton(sp => new DatabaseMaintenance(
            config.GetConnectionString("Sqlite"),
            sp.GetRequiredService<MaintenanceSettings>()));
        services.AddSingleton<DailyBackupHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<DailyBackupHostedService>());
        services.AddSignalR();
        services.AddBrmesPolicies();
        services.AddSingleton<IExecutionPublisher, SignalRExecutionPublisher>();

        // 签发（JwtIssuer.Issue）与验证两侧必须用同一个密钥解析规则，否则会出现
        // "配置缺失时验证侧悄悄换 fallback、签发侧却直接抛异常"的不对称行为。
        var jwtKey = config["Jwt:Key"];
        if (env.IsDevelopment())
        {
            // 开发环境允许缺省密钥，保证 clone 下来即可运行。
            jwtKey ??= "dev-only-change-me-32bytes-minimum-key!!";
        }
        else
        {
            // 占位密钥意味着任何人都能离线伪造管理员令牌，宁可启动失败。
            if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Contains("change-me", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Jwt:Key 未配置或仍是开发占位值：非开发环境必须提供显式密钥（appsettings.Production.json 或环境变量 Jwt__Key）。");
            if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
                throw new InvalidOperationException("Jwt:Key 强度不足：HS256 对称密钥至少需要 32 字节。");
        }
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = config["Jwt:Issuer"] ?? "RecipesManage",
                    ValidAudience = config["Jwt:Audience"] ?? "RecipesManage",
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) &&
                            context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });
        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// 备份配置：每一项都能缺省，单设备现场常常没人改 appsettings。
    /// AtUtc 只认不变的 "HH:mm" 格式——几点备份是运维事实，不该被服务器区域设置读成另一个时刻。
    /// </summary>
    /// <summary>
    /// 维护配置。两个阈值决定"什么时候肯为省磁盘重写整个库文件"：
    /// 空闲页占比不够就不动手，绝对量太小也不动手（10 MB 的库白忙一场，还要冒一次全文件重写的风险）。
    /// </summary>
    private static MaintenanceSettings MaintenanceSettingsFrom(IConfiguration config) => new()
    {
        Enabled = config.GetValue("Maintenance:Enabled", true),
        Vacuum = config.GetValue("Maintenance:Vacuum", true),
        MinFreeRatio = Math.Clamp(config.GetValue("Maintenance:MinFreeRatio", 0.2), 0.01, 1),
        MinFreeMegabytes = Math.Max(1, config.GetValue("Maintenance:MinFreeMegabytes", 64)),
        BusyTimeoutMs = Math.Clamp(config.GetValue("Maintenance:BusyTimeoutMs", 30_000), 0, 300_000)
    };

    private static BackupSettings BackupSettingsFrom(IConfiguration config)
    {
        var at = TimeOnly.TryParseExact(config["Backup:AtUtc"], "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed : new TimeOnly(2, 15);
        var directory = config["Backup:Directory"];
        return new BackupSettings
        {
            Enabled = config.GetValue("Backup:Enabled", true),
            AtUtc = at,
            Keep = config.GetValue("Backup:Keep", 7),
            KeepPreMigration = config.GetValue("Backup:KeepPreMigration", 3),
            Directory = string.IsNullOrWhiteSpace(directory) ? "App_Data/backups" : directory.Trim()
        };
    }
}

public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;
    public HttpCurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string UserName => Principal?.Identity?.Name ?? "";
    public string DisplayName => Principal?.FindFirstValue("displayName") ?? UserName;
    public UserRole? Role => Enum.TryParse<UserRole>(Principal?.FindFirstValue(ClaimTypes.Role), out var role) ? role : null;
}

public static class JwtIssuer
{
    public static string Issue(IConfiguration config, Domain.Identity.AppUser user)
    {
        var key = config["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key missing");
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        var hours = double.TryParse(config["Jwt:ExpireHours"], out var h) ? h : 12;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UserName),
            new Claim("displayName", user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };
        var token = new JwtSecurityToken(
            config["Jwt:Issuer"] ?? "RecipesManage",
            config["Jwt:Audience"] ?? "RecipesManage",
            claims,
            expires: DateTime.UtcNow.AddHours(hours),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
