using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Application.Services;

/// <summary>
/// 电子签名与角色守卫的单一实现。此前 BatchService / MaterialLotService / RecipeService
/// 各自复制了一份，语义漂移一次就是 GMP 审计缺陷。
/// 名单是闭集：Admin 必须写进 <see cref="Capabilities"/> 才有权，不能靠通配代签启停/跳步。
/// 角色名单本身不在这里——见 <see cref="Capabilities"/>。
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

    /// <summary>
    /// 业务代码统一入口：按 <see cref="Capabilities"/> 里的单一名单判断当前角色。
    /// <paramref name="deniedMessage"/> 仅用于需要沿用旧提示文案的位置（前端按文案做了翻译）。
    /// </summary>
    /// <summary>
    /// 记下一次已验过密码的电子签名：结构化记录（含义原文在此冻结）+ 一行审计履历，随调用方同一次 SaveChanges 提交。
    /// 必须在 <see cref="RequireAsync"/> 通过之后调用——这里不验密码，只负责落库。
    /// </summary>
    /// <param name="meaning">签署当时展示给签名人的含义原文；不要传"之后再算"的东西。</param>
    /// <param name="signerName">默认当前登录人；个别路径需要固定署名时才传。</param>
    public void Record(
        string action, string entityType, string entityId, string meaning, string? detail, string? signerName = null,
        int? contentHashVersion = null, string? contentHash = null)
    {
        var name = signerName ?? user.UserName;
        db.SignatureRecords.Add(new SignatureRecord(
            user.UserId, name, action, entityType, entityId, meaning, detail, contentHashVersion, contentHash));
        var trimmed = detail?.Trim();
        db.AuditLogs.Add(new AuditLog(
            user.UserId, name, action, entityType, entityId,
            string.IsNullOrEmpty(trimmed) ? meaning : $"{meaning} {trimmed}"));
    }

    public void EnsureCan(Capability capability, string? deniedMessage = null) =>
        user.EnsureCan(capability, deniedMessage);

    /// <summary>
    /// 仅保留给测试与特殊场景（审批链节点的角色是运行期数据，没有对应的静态能力）。
    /// 业务代码请用 <see cref="EnsureCan"/>，不要再手写角色列表。
    /// </summary>
    public void EnsureRole(params UserRole[] allowed) =>
        user.EnsureCan(new Capability("adhoc", allowed));
}
