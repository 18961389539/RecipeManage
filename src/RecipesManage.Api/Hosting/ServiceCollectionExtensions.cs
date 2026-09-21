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

namespace RecipesManage.Api;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRecipesManage(this IServiceCollection services, IConfiguration config, IWebHostEnvironment env)
    {
        services.AddDbContext<AppDbContext>(options =>
        {
            RecipesDatabase.Apply(options, config.GetConnectionString("PostgreSQL"), config.GetConnectionString("Sqlite"));
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        });

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<AuthService>();
        services.AddScoped<RecipeService>();
        services.AddScoped<MaterialLotService>();
        services.AddScoped<BatchService>();
        services.AddScoped<EquipmentService>();
        services.AddScoped<AuditService>();
        services.AddScoped<EquipmentLeaseService>();
        services.AddSingleton<SimulatedPlcRack>();
        services.AddSingleton<ModbusTcpHandshakeSlave>();
        services.AddHostedService<ModbusLoopbackHostedService>();
        services.AddSingleton<OpcUaHandshakeSlave>();
        services.AddHostedService<OpcUaLoopbackHostedService>();
        services.AddSingleton<SiemensS7HandshakeSlave>();
        services.AddHostedService<SiemensS7LoopbackHostedService>();
        services.AddSingleton<IPlcDriverFactory, PlcDriverFactory>();
        services.AddSingleton<IBatchRecordPdf, BatchRecordPdf>();
        services.AddSingleton<BatchSchedulerHostedService>();
        services.AddSingleton<IBatchScheduler>(sp => sp.GetRequiredService<BatchSchedulerHostedService>());
        services.AddHostedService(sp => sp.GetRequiredService<BatchSchedulerHostedService>());
        services.AddSignalR();
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
