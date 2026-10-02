using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Application.Services;

public static class CapabilityExtensions
{
    public const string DefaultDeniedMessage = "当前角色无权执行该操作。";

    /// <summary>
    /// 服务层二次校验的唯一实现（控制器上的授权策略是第一道，同样读 <see cref="Capabilities"/>）。
    /// <paramref name="deniedMessage"/> 只用于要沿用既有提示文案的位置——前端按文案做了翻译。
    /// </summary>
    public static void EnsureCan(this ICurrentUser user, Capability capability, string? deniedMessage = null)
    {
        var role = user.Role ?? throw new DomainException("AUTH", "未登录。");
        if (!capability.Allows(role))
            throw new DomainException("FORBIDDEN", deniedMessage ?? DefaultDeniedMessage);
    }
}
