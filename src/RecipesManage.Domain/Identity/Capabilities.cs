namespace RecipesManage.Domain.Identity;

/// <summary>
/// 一项"能力"：谁能做这件事。名单是闭集——不在里面的角色（包括 Admin）一律不行，
/// 不存在"管理员通配"，否则就能代签启停、跳步这类需要职责分离的操作。
/// </summary>
public sealed record Capability(string Key, IReadOnlyList<UserRole> Roles)
{
    public bool Allows(UserRole role) => Roles.Contains(role);
}

/// <summary>
/// 授权名单的唯一来源。
///
/// 为什么放域层：以前同一份名单抄在两处——Api 的 <c>AuthorizationPolicies</c>（进业务前拒绝、在 OpenAPI 里可见）
/// 和 Application 各服务里的 <c>EnsureRole(...)</c>（服务被别处直接调用时自保），共约 30 处手写角色列表。
/// 两边靠人对照，而它们真的漂移过：注入仿真故障的策略放行了 Admin，服务层却拒绝 Admin，
/// 结果是 Admin 过了第一道门、在第二道才被 FORBIDDEN。
///
/// 现在两道门读同一份定义：策略从 <see cref="All"/> 生成，服务调用 <c>EnsureCan(Capabilities.X)</c>。
/// 纵深防御保留（电子签名路径不能只靠一层），但名单只写这一次。
/// 改角色权限 = 只改这个文件；<c>CapabilitiesTests</c> 把关键的职责分离规则钉成测试。
/// </summary>
public static class Capabilities
{
    // 键名同时是授权策略名，Api 里的常量别名到这些键（属性参数必须是编译期常量，所以单列一组 const）。
    public const string AdminKey = "admin";
    public const string RecipeAuthorKey = "recipe.author";
    public const string RecipeExportKey = "recipe.export";
    public const string BatchOperateKey = "batch.operate";
    public const string BatchSkipKey = "batch.skip";
    public const string BatchConfirmKey = "batch.confirm";
    public const string QualityDispositionKey = "quality.disposition";
    public const string AlarmAckKey = "alarm.ack";
    public const string LotReceiveKey = "lot.receive";
    public const string LotHandleKey = "lot.handle";
    public const string EquipmentAdminKey = "equipment.admin";
    public const string EquipmentOperateKey = "equipment.operate";
    public const string EquipmentSimulateKey = "equipment.simulate";
    public const string PhaseLibraryKey = "phase.library";
    public const string AuditViewKey = "audit.view";
    public const string BatchRecordViewKey = "batch.record.view";

    private static Capability Of(string key, params UserRole[] roles) => new(key, roles);

    /// <summary>管理员：用户、审批链配置、系统维护。</summary>
    public static readonly Capability Admin = Of(AdminKey, UserRole.Admin);

    /// <summary>工艺工程师：起草、保存工艺、提交审核、升版、导入。</summary>
    public static readonly Capability RecipeAuthor = Of(RecipeAuthorKey, UserRole.ProcessEngineer);

    /// <summary>导出配方包：工程师之外，质量与主管也要能取档审阅。</summary>
    public static readonly Capability RecipeExport =
        Of(RecipeExportKey, UserRole.ProcessEngineer, UserRole.Quality, UserRole.Supervisor);

    /// <summary>操作员 / 主管：创建批次、启动、保持、恢复、中止。</summary>
    public static readonly Capability BatchOperate = Of(BatchOperateKey, UserRole.Operator, UserRole.Supervisor);

    /// <summary>主管：跳过工步（会改变已冻结控制配方的执行路径）。</summary>
    public static readonly Capability BatchSkip = Of(BatchSkipKey, UserRole.Supervisor);

    /// <summary>操作员 / 主管 / 质量：人工确认工步。</summary>
    public static readonly Capability BatchConfirm =
        Of(BatchConfirmKey, UserRole.Operator, UserRole.Supervisor, UserRole.Quality);

    /// <summary>仅质量：放行、拒收、实验室样品判定。任何角色都不能代签。</summary>
    public static readonly Capability QualityDisposition = Of(QualityDispositionKey, UserRole.Quality);

    public static readonly Capability AlarmAck =
        Of(AlarmAckKey, UserRole.Operator, UserRole.Supervisor, UserRole.Quality);

    /// <summary>来料登记：所有角色都可以录入仓。</summary>
    public static readonly Capability LotReceive = Of(
        LotReceiveKey, UserRole.Admin, UserRole.Operator, UserRole.Supervisor, UserRole.Quality, UserRole.ProcessEngineer);

    /// <summary>拆分子批、登记样品（工艺工程师不参与实物操作）。</summary>
    public static readonly Capability LotHandle =
        Of(LotHandleKey, UserRole.Admin, UserRole.Operator, UserRole.Supervisor, UserRole.Quality);

    /// <summary>仅管理员：新建/修改设备与握手点表。</summary>
    public static readonly Capability EquipmentAdmin = Of(EquipmentAdminKey, UserRole.Admin);

    /// <summary>管理员 / 操作员 / 主管：测试 PLC 连接（只读探测）。</summary>
    public static readonly Capability EquipmentOperate =
        Of(EquipmentOperateKey, UserRole.Admin, UserRole.Operator, UserRole.Supervisor);

    /// <summary>
    /// 操作员 / 主管：注入、清除仿真故障（会改变现场设备状态，所以管理员不在其中）。
    /// 以前它与 <see cref="EquipmentOperate"/> 共用策略，策略放行 Admin 而服务层拒绝 Admin；
    /// 单列之后两层口径一致，Admin 在第一道门就被拒。
    /// </summary>
    public static readonly Capability EquipmentSimulate =
        Of(EquipmentSimulateKey, UserRole.Operator, UserRole.Supervisor);

    /// <summary>管理员 / 工艺工程师：相模板库。</summary>
    public static readonly Capability PhaseLibrary = Of(PhaseLibraryKey, UserRole.Admin, UserRole.ProcessEngineer);

    /// <summary>
    /// 质量 / 管理员：全局审计日志。GMP 审计追踪的读者是质量与系统管理，
    /// 不对车间通览开放——其余业务数据（配方 / 批次 / 设备 / 物料）仍是登录即可。
    /// </summary>
    public static readonly Capability AuditView = Of(AuditViewKey, UserRole.Quality, UserRole.Admin);

    /// <summary>
    /// 主管 / 质量 / 管理员：电子批记录与 PDF 导出。批记录是归档凭据（含签名与检验数据），
    /// 给生产监督与质量；操作员在监控页看实时执行，不再持有归档件的查看权。
    /// </summary>
    public static readonly Capability BatchRecordView =
        Of(BatchRecordViewKey, UserRole.Supervisor, UserRole.Quality, UserRole.Admin);

    public static IReadOnlyList<Capability> All { get; } =
    [
        Admin, RecipeAuthor, RecipeExport, BatchOperate, BatchSkip, BatchConfirm, QualityDisposition,
        AlarmAck, LotReceive, LotHandle, EquipmentAdmin, EquipmentOperate, EquipmentSimulate, PhaseLibrary,
        AuditView, BatchRecordView,
    ];
}
