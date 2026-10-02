using Microsoft.AspNetCore.Authorization;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Api;

/// <summary>
/// 授权策略的注册点。角色名单不在这里——唯一来源是 <see cref="Capabilities"/>（Domain），
/// 策略由它生成，Application 服务层的二次校验读同一份，两道门不会再各写各的。
///
/// 为什么要有第一道门：此前全仓库没有一处 <c>[Authorize(Roles=…)]</c>，角色判定只在 Application 服务里，
/// 而 <c>EquipmentService</c> 一个都没有，实测车间操作员令牌可以直接 <c>POST /api/equipment</c> 建/改 PLC 设备
/// （界面把按钮藏起来了 ≠ 接口关着）。策略把拒绝提前到进入业务逻辑之前，并在 OpenAPI 里可见；
/// 服务层校验保留，服务被别处直接调用时仍自保。
///
/// 这里的常量只是给 <c>[Authorize(Policy = …)]</c> 用的编译期别名（属性参数必须是 const）。
/// 新增能力：在 <see cref="Capabilities"/> 加键、名单并登记进 <c>All</c>（<c>CapabilitiesTests</c> 用反射检查不会漏登记），
/// 再在这里加一个别名即可；别名写错键名是编译错误。
/// </summary>
public static class AuthorizationPolicies
{
    public const string Admin = Capabilities.AdminKey;
    public const string RecipeAuthor = Capabilities.RecipeAuthorKey;
    public const string RecipeExport = Capabilities.RecipeExportKey;
    public const string BatchOperate = Capabilities.BatchOperateKey;
    public const string BatchSkip = Capabilities.BatchSkipKey;
    public const string BatchConfirm = Capabilities.BatchConfirmKey;
    public const string QualityDisposition = Capabilities.QualityDispositionKey;
    public const string AlarmAck = Capabilities.AlarmAckKey;
    public const string LotReceive = Capabilities.LotReceiveKey;
    public const string LotHandle = Capabilities.LotHandleKey;
    public const string EquipmentAdmin = Capabilities.EquipmentAdminKey;
    public const string EquipmentOperate = Capabilities.EquipmentOperateKey;
    public const string EquipmentSimulate = Capabilities.EquipmentSimulateKey;
    public const string PhaseLibrary = Capabilities.PhaseLibraryKey;

    public static IServiceCollection AddBrmesPolicies(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            foreach (var capability in Capabilities.All)
            {
                options.AddPolicy(capability.Key, new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .RequireRole(capability.Roles.Select(r => r.ToString()))
                    .Build());
            }
        });
        return services;
    }
}
