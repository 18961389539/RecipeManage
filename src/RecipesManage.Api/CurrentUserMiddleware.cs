using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using RecipesManage.Domain.Identity;
using RecipesManage.Infrastructure.Persistence;

namespace RecipesManage.Api;

/// <summary>
/// 把「当前身份」从 JWT 声明改回数据库里的事实。
///
/// 为什么需要：令牌里带的是签发那一刻的用户名与角色，默认 12 小时才过期
/// （Jwt:ExpireHours）。管理员停用账号或改角色之后，旧令牌在自然过期前完全照常生效——
/// 被停用的质量工程师仍能用自己的密码完成电子签名并放行批次，而电子签名只验密码、
/// 密码停用后依然是对的。
///
/// 所以每个已认证请求按主键读一次 users 表（个位数行量）：停用/已删即 401，
/// 角色与显示名以库为准重写回 ClaimsPrincipal，下游 ICurrentUser.Role 与授权判定读到的都是新值。
///
/// 边界：已经建立的 SignalR 连接不受影响（握手时才走这条管道），
/// 停用后对端的推送会一直活到它自己重连为止——写数据的路径都在 HTTP 上，这里挡住即可。
/// </summary>
public sealed class CurrentUserMiddleware(RequestDelegate next, ILogger<CurrentUserMiddleware> log)
{
    public async Task Invoke(HttpContext context, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var idClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idClaim, out var userId))
        {
            await Reject(context, "令牌缺少用户标识。");
            return;
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, context.RequestAborted);
        if (user is null)
        {
            await Reject(context, "账号已不存在，请重新登录。");
            return;
        }
        if (!user.IsActive)
        {
            await Reject(context, "账号已停用，请联系管理员。");
            return;
        }

        context.User = WithCurrentProfile(context.User, user);
        await next(context);
    }

    /// <summary>
    /// 只替换会过期的三类声明（角色、显示名、用户名），其余原样保留。
    ///
    /// 注意 nameType / roleType 必须沿用来源身份自己的那一对：ClaimsIdentity 的 Name 是按
    /// nameType 去取声明的，早期版本这里传了 ClaimTypes.NameIdentifier，结果 Identity.Name
    /// 变成用户 GUID，审计里 user.update 的操作人就被写成了 GUID。
    /// </summary>
    private static ClaimsPrincipal WithCurrentProfile(ClaimsPrincipal principal, AppUser user)
    {
        if (principal.Identity is not ClaimsIdentity identity) return principal;

        var fresh = new ClaimsIdentity(identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType);
        var replaced = new HashSet<string>
        {
            identity.NameClaimType, identity.RoleClaimType,
            ClaimTypes.Name, ClaimTypes.Role,
            "displayName"
        };
        foreach (var claim in identity.Claims)
        {
            if (replaced.Contains(claim.Type)) continue;
            fresh.AddClaim(claim);
        }
        fresh.AddClaim(new Claim(identity.NameClaimType, user.UserName));
        fresh.AddClaim(new Claim(identity.RoleClaimType, user.Role.ToString()));
        fresh.AddClaim(new Claim("displayName", user.DisplayName));
        return new ClaimsPrincipal(fresh);
    }

    private async Task Reject(HttpContext context, string message)
    {
        // 401 而不是 403：前端的会话过期处理会清令牌并带回登录页，
        // 停在页内报「角色无权」会让人以为是自己权限配错，反复找管理员。
        log.LogWarning("会话失效：{Path} {Message}", context.Request.Path, message);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { code = "SESSION_INVALID", message });
    }
}
