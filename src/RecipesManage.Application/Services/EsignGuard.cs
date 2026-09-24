using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Application.Services;

/// <summary>
/// 电子签名与角色守卫的单一实现。此前 BatchService / MaterialLotService / RecipeService
/// 各自复制了一份，语义漂移一次就是 GMP 审计缺陷。
/// 名单是闭集：Admin 必须写进允许角色才有权，不能靠通配代签启停/跳步。
/// 质量放行/审核节点继续调用 <see cref="EnsureExactRole"/>，标明「任何角色都不能代签」。
/// </summary>
public sealed class EsignGuard(IAppDbContext db, ICurrentUser user, IPasswordHasher passwords)
{
    public async Task RequireAsync(string? password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new DomainException("ESIGN", "必须重新输入登录密码作为电子签名。");
        var signer = await db.Users.FirstOrDefaultAsync(u => u.Id == user.UserId, ct)
                     ?? throw new DomainException("AUTH", "未登录。");
        // 密码在停用后依然是对的，所以"验过密码"不等于"这个人有资格签"。
        // 请求管道里已按当前用户复核过，这里是签名路径上的第二道，防止别处绕过中间件。
        if (!signer.IsActive)
            throw new DomainException("ESIGN", "该账号已停用，不能作为电子签名人。");
        if (!passwords.Verify(password, signer.PasswordHash))
            throw new DomainException("ESIGN", "电子签名密码不正确。");
    }

    public void EnsureRole(params UserRole[] allowed) => EnsureExactRole(allowed);

    public void EnsureExactRole(params UserRole[] allowed)
    {
        var role = user.Role ?? throw new DomainException("AUTH", "未登录。");
        if (!allowed.Contains(role))
            throw new DomainException("FORBIDDEN", "当前角色无权执行该操作。");
    }
}
