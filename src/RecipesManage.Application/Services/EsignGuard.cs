using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Application.Services;

/// <summary>
/// 电子签名与角色守卫的单一实现。此前 BatchService / MaterialLotService / RecipeService
/// 各自复制了一份，语义漂移一次就是 GMP 审计缺陷。
/// Admin 可跨角色名单（运维兜底），但职责分离在域层 RecipeVersion.Decide 强制，任何角色都绕不过。
/// </summary>
public sealed class EsignGuard(IAppDbContext db, ICurrentUser user, IPasswordHasher passwords)
{
    public async Task RequireAsync(string? password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new DomainException("ESIGN", "必须重新输入登录密码作为电子签名。");
        var signer = await db.Users.FirstOrDefaultAsync(u => u.Id == user.UserId, ct)
                     ?? throw new DomainException("AUTH", "未登录。");
        if (!passwords.Verify(password, signer.PasswordHash))
            throw new DomainException("ESIGN", "电子签名密码不正确。");
    }

    public void EnsureRole(params UserRole[] allowed)
    {
        var role = user.Role ?? throw new DomainException("AUTH", "未登录。");
        if (role != UserRole.Admin && !allowed.Contains(role))
            throw new DomainException("FORBIDDEN", "当前角色无权执行该操作。");
    }
}
