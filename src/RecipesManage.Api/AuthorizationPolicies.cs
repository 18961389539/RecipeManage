using Microsoft.AspNetCore.Authorization;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Api;

/// <summary>
/// 授权策略名单的唯一来源。
///
/// 为什么要有这个文件：此前全仓库没有一处 <c>[Authorize(Roles=…)]</c>，角色判定散在
/// Application 层的 11 个方法里 —— 而 <c>EquipmentService</c> 一个都没有，
/// 实测车间操作员令牌可以直接 <c>POST /api/equipment</c> 建/改 PLC 设备与点表
/// （界面把按钮藏起来了 ≠ 接口关着）。
///
/// 两条原则：
/// - 策略名按"能力"命名而不是按角色命名，角色名单只在这里出现一次；
/// - Application 层原有的 <c>EnsureRole</c> / <c>EnsureExactRole</c> 全部保留，
///   这一层只是把拒绝提前到进入业务逻辑之前，并在 OpenAPI 里可见。服务被别处直接调用时仍自保。
/// </summary>
public static class AuthorizationPolicies
{
    public const string Admin = "admin";

    /// <summary>工艺工程师：起草、保存工艺、提交审核、升版、导入。</summary>
    public const string RecipeAuthor = "recipe.author";

    /// <summary>导出配方包：工程师之外，质量与主管也要能取档审阅（与 RecipeService.ExportAsync 同名单）。</summary>
    public const string RecipeExport = "recipe.export";

    /// <summary>操作员 / 主管：创建批次、启动、保持、恢复、中止。</summary>
    public const string BatchOperate = "batch.operate";

    /// <summary>主管：跳过工步（会改变已冻结控制配方的执行路径）。</summary>
    public const string BatchSkip = "batch.skip";

    /// <summary>操作员 / 主管 / 质量：人工确认工步。</summary>
    public const string BatchConfirm = "batch.confirm";

    /// <summary>仅质量：放行、拒收、实验室样品判定。任何角色都不能代签。</summary>
    public const string QualityDisposition = "quality.disposition";

    public const string AlarmAck = "alarm.ack";

    /// <summary>来料登记：所有角色都可以录入仓。</summary>
    public const string LotReceive = "lot.receive";

    /// <summary>拆分子批、登记样品（工艺工程师不参与实物操作）。</summary>
    public const string LotHandle = "lot.handle";

    /// <summary>仅管理员：新建/修改设备与握手点表。</summary>
    public const string EquipmentAdmin = "equipment.admin";

    /// <summary>操作员及以上：测试连接、注入/清除仿真故障（会改变现场设备状态）。</summary>
    public const string EquipmentOperate = "equipment.operate";

    /// <summary>管理员 / 工艺工程师：相模板库。</summary>
    public const string PhaseLibrary = "phase.library";

    private static AuthorizationPolicy RolePolicy(params UserRole[] roles) =>
        new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
            .RequireRole(roles.Select(r => r.ToString())).Build();

    public static IServiceCollection AddBrmesPolicies(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy(Admin, RolePolicy(UserRole.Admin));
            options.AddPolicy(RecipeAuthor, RolePolicy(UserRole.ProcessEngineer));
            options.AddPolicy(RecipeExport, RolePolicy(UserRole.ProcessEngineer, UserRole.Quality, UserRole.Supervisor));
            options.AddPolicy(BatchOperate, RolePolicy(UserRole.Operator, UserRole.Supervisor));
            options.AddPolicy(BatchSkip, RolePolicy(UserRole.Supervisor));
            options.AddPolicy(BatchConfirm, RolePolicy(UserRole.Operator, UserRole.Supervisor, UserRole.Quality));
            options.AddPolicy(QualityDisposition, RolePolicy(UserRole.Quality));
            options.AddPolicy(AlarmAck, RolePolicy(UserRole.Operator, UserRole.Supervisor, UserRole.Quality));
            options.AddPolicy(LotReceive, RolePolicy(
                UserRole.Admin, UserRole.Operator, UserRole.Supervisor, UserRole.Quality, UserRole.ProcessEngineer));
            options.AddPolicy(LotHandle, RolePolicy(
                UserRole.Admin, UserRole.Operator, UserRole.Supervisor, UserRole.Quality));
            options.AddPolicy(EquipmentAdmin, RolePolicy(UserRole.Admin));
            options.AddPolicy(EquipmentOperate, RolePolicy(UserRole.Admin, UserRole.Operator, UserRole.Supervisor));
            options.AddPolicy(PhaseLibrary, RolePolicy(UserRole.Admin, UserRole.ProcessEngineer));
        });
        return services;
    }
}
