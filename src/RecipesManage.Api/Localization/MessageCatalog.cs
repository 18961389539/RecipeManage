using System.Globalization;

namespace RecipesManage.Api.Localization;

/// <summary>
/// 后端返回给界面的**瞬时提示**英文表。键 = 中文原文（与前端 i18n 同一套做法）。
///
/// 只管这里，不管别处：审计痕迹、电子签名含义、握手事件明细、快照里的节点名都是
/// **落库原文**，读取时再翻译会让同一份批记录在不同人屏幕上显示不同内容，
/// 那是审计事实而不是界面文案，所以永不进这张表。
///
/// 为什么不覆盖带变量的消息（约 53 条，如「批次 {no} 不存在。」）：
/// 精确文本匹配对它们无效，而半中半英的一句（"Batch B-7 不存在。"）比整句中文更难读。
/// 那部分要真的双语，得给 DomainException 加消息模板与参数——那是改 250 个抛出点的活，
/// 单独一轮做，不要在这里用正则糊过去。缺译的表现是整句中文，可预期、可搜索。
///
/// 文案改动会让键失配，而失配只是退回中文、不会报错，所以 Contracts.Tests 有一条
/// "每个键都还能在源码里找到"的断言兜着（见 BackendMessageCatalogTests）。
/// </summary>
public static class MessageCatalog
{
    /// <summary>只认明确的 en；其余（含缺省）保持中文原样，避免浏览器语言偏好悄悄改变服务器输出。</summary>
    public static string Localize(string message, CultureInfo? culture) =>
        culture?.Name is not ("en" or "en-US" or "en-GB") ? message
        : En.TryGetValue(message, out var translated) ? translated : message;

    private static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        // ---- 授权与身份 ----
        ["当前角色无权执行该操作。"] = "Your role is not allowed to perform this operation.",
        ["未登录。"] = "Not signed in.",
        ["仅管理员可以管理用户。"] = "Only an administrator can manage users.",
        ["仅管理员可配置设备与 PLC 点表。"] = "Only an administrator can configure equipment and PLC tag maps.",
        ["仅管理员或工艺工程师可维护设备类相库。"] = "Only an administrator or process engineer can maintain the phase library.",
        ["仅车间操作员或工艺主管可注入仿真故障。"] = "Only an operator or process supervisor can inject simulator faults.",
        ["用户名或密码错误。"] = "Incorrect username or password.",
        ["该账号已停用，不能作为电子签名人。"] = "This account is deactivated and cannot be used as a signer.",
        ["不能停用当前登录账号。"] = "You cannot deactivate the account you are signed in with.",
        ["不能停用最后一个启用的管理员。"] = "You cannot deactivate the last active administrator.",
        ["密码至少 8 位。"] = "The password must be at least 8 characters.",
        ["用户名至少 3 个字符。"] = "The username must be at least 3 characters.",
        ["用户名已存在。"] = "That username already exists.",
        ["用户不存在。"] = "User not found.",
        ["必须重新输入登录密码作为电子签名。"] = "Re-enter your sign-in password as the electronic signature.",
        ["电子签名密码不正确。"] = "The e-signature password is incorrect.",

        // ---- 配方与审核 ----
        ["保存工艺必须填写变更原因。"] = "A change reason is required to save the procedure.",
        ["工步为空，不能提交审核。"] = "The procedure has no steps, so it cannot be submitted for review.",
        ["当前版本不在审核中。"] = "This version is not under review.",
        ["没有待审核版本。"] = "There is no version awaiting review.",
        ["本版本已无待处理的审核节点。"] = "This version has no pending review nodes.",
        ["该审核节点已处理。"] = "This review node has already been handled.",
        ["审核结论无效。"] = "The review decision is not valid.",
        ["没有被驳回的版本。"] = "There is no rejected version.",
        ["只有被驳回的版本可以重新打开为草稿。"] = "Only a rejected version can be reopened as a draft.",
        ["已存在草稿或审核中的版本，不能并行开版。"] = "A draft or in-review version already exists; versions cannot be opened in parallel.",
        ["当前没有可编辑的草稿版本，请先从已批准版本创建新版本。"] =
            "There is no editable draft. Create a new version from an approved one first.",
        ["没有已批准版本可供升版。"] = "No approved version is available to revise.",
        ["没有已批准的主配方版本，无法生成控制配方。"] = "No approved master recipe version exists, so a control recipe cannot be generated.",
        ["只能从已批准的主配方版本生成控制配方快照。"] =
            "Control recipe snapshots can only be generated from an approved master recipe version.",
        ["配方不存在。"] = "Recipe not found.",
        ["配方编码已存在。"] = "That recipe code already exists.",
        ["版本不属于该配方。"] = "This version does not belong to that recipe.",
        ["职责分离：该用户已在此版本的提交或审核链条中署名，不能继续担任本审核节点。"] =
            "Segregation of duties: this user already appears in the submission or review chain of this version and cannot act as this reviewer.",

        // ---- 审批链配置 ----
        ["审批链不存在。"] = "The approval chain does not exist.",
        ["要改的审批链不存在。"] = "The approval chain being edited does not exist.",
        ["审批链编码不能为空。"] = "The approval chain code is required.",
        ["审批链名称不能为空。"] = "The approval chain name is required.",
        ["必须有一条默认链；请把默认让给另一条，而不是取消它。"] =
            "A default chain is required; hand the default over to another chain instead of clearing it.",
        ["默认链不能停用；先把默认让给另一条。"] = "The default chain cannot be disabled; hand the default over first.",
        ["默认链不能删除；先把默认让给另一条。"] = "The default chain cannot be deleted; hand the default over first.",

        // ---- 工艺拓扑与相库 ----
        ["工艺拓扑存在环路，无法按顺序下发 PLC。"] = "The procedure topology contains a cycle, so steps cannot be issued to the PLC in order.",
        ["单元规程拓扑存在环路。"] = "The unit procedure topology contains a cycle.",
        ["工步不能自环。"] = "A step cannot link to itself.",
        ["存在指向未知工步的连线。"] = "A link points to an unknown step.",
        ["设备类不存在。"] = "Equipment class not found.",
        ["设备类编码不能为空。"] = "The equipment class code is required.",
        ["相模板不存在。"] = "Phase template not found.",
        ["相模板编码不能为空。"] = "The phase template code is required.",
        ["设备类相模板必须是写 PLC 的工艺相，等待/质检/人工确认由上位机执行。"] =
            "Equipment class phase templates must be PLC-writing process phases; wait, quality and manual-confirm phases are executed by the host.",

        // ---- 设备与点表 ----
        ["设备不存在。"] = "Equipment not found.",
        ["设备编码不能为空。"] = "The equipment code is required.",
        ["设备名称不能为空。"] = "The equipment name is required.",
        ["设备编码已存在。"] = "That equipment code already exists.",
        ["设备未启用。"] = "The equipment is not enabled.",
        ["绑定设备已有批次在执行、排队或保持。"] = "The bound equipment already has a batch running, queued or held.",
        ["单元绑定的设备不存在。"] = "The equipment bound to the unit procedure does not exist.",
        ["点表必须包含 PLC_Ready 与 Trigger_Write，禁止无握手地址盲写。"] =
            "The tag map must define PLC_Ready and Trigger_Write; blind writes without a handshake address are forbidden.",
        ["点表必须包含 Host_Hold 与 PLC_Held，禁止无应答保持。"] =
            "The tag map must define Host_Hold and PLC_Held; holding without an acknowledgement is forbidden.",
        ["Params 槽位地址不能为空。"] = "Parameter slot addresses cannot be empty.",
        ["实测点名称不能为空。"] = "Measured point names cannot be empty.",
        ["只能对 Simulator 注入握手故障，真实 PLC 禁止此操作。"] =
            "Handshake faults can only be injected on the simulator; this is refused on real PLCs.",
        ["故障模式无效。"] = "The fault mode is not valid.",

        // ---- 批次与执行 ----
        ["批次不存在。"] = "Batch not found.",
        ["批次号已存在。"] = "That batch number already exists.",
        ["批次缩放因子必须为正数。"] = "The batch scale factor must be positive.",
        ["控制配方快照损坏。"] = "The control recipe snapshot is corrupt.",
        ["控制配方快照完整性失败，禁止启动或放行。"] =
            "The control recipe snapshot failed its integrity check; starting or releasing is not permitted.",
        ["控制配方快照没有可执行工步。"] = "The control recipe snapshot has no executable steps.",
        ["指定工步不在本批次控制配方快照中。"] = "The specified step is not in this batch's control recipe snapshot.",
        ["只有保持中的批次可以恢复。"] = "Only a held batch can be resumed.",
        ["已完成、已中止或已处置的批次不能再次中止。"] = "A completed, aborted or dispositioned batch cannot be aborted again.",
        ["只能跳过正在等待 PLC_Ready、等待或人工确认的工步，禁止跨单元误跳邻道。"] =
            "Only steps waiting for PLC_Ready, waiting, or awaiting manual confirmation may be skipped; skipping another lane across units is refused.",
        ["当前工步已完成，不能跳过。"] = "The current step is finished and cannot be skipped.",
        ["该车道还没有被执行引擎接管，不能跳步。请等批次进入握手状态后再试。"] =
            "This lane has not been taken over by the execution engine yet, so it cannot be skipped. Wait until the batch enters the handshake state.",
        ["只有运行中的批次可以人工确认。"] = "Only a running batch can take a manual confirmation.",
        ["当前工步不是人工确认，禁止当作写参工步确认。"] =
            "The current step is not a manual-confirm step; it cannot be confirmed as if it wrote to the PLC.",
        ["当前没有等待确认的人工确认工步。"] = "There is no manual-confirm step awaiting confirmation.",
        ["该人工确认工步已结束。"] = "This manual-confirm step has already ended.",
        ["批次状态已被调度引擎并发更新，请刷新后重试。"] =
            "The batch state was updated concurrently by the scheduling engine; refresh and try again.",
        ["终检样品尚未判定，不能放行。"] = "The final sample has not been judged, so the batch cannot be released.",
        ["批记录证据已变化或摘要版本不支持，请刷新并重新核对后签署。"] =
            "The batch-record evidence changed or its hash version is unsupported. Refresh and review it before signing.",
        ["实验室样品签名内容校验失败，不能质量放行。"] =
            "A lab-sample signature failed content verification; the batch cannot be released.",

        // ---- 样品与质检 ----
        ["样品不存在。"] = "Sample not found.",
        ["样品不属于该生产批次。"] = "This sample does not belong to that production batch.",
        ["样品编号不能为空。"] = "The sample number is required.",
        ["样品编号已存在。"] = "That sample number already exists.",
        ["判定不能仍为待检。"] = "The disposition cannot remain pending.",
        ["不合格样品必须填写对照规格的意见。"] = "A failing sample requires a comment against the specification.",
        ["拒收必须填写对照质检与握手归档的意见。"] =
            "Rejection requires a comment covering the archived quality results and handshake.",
        ["归档质检或实验室样品超差，偏差放行必须填写意见。"] =
            "Archived quality results or lab samples are out of specification; a deviation release requires a comment.",

        // ---- 物料批次 ----
        ["物料批次不存在。"] = "Material lot not found.",
        ["物料批次号不能为空。"] = "The material lot number is required.",
        ["物料批次号已存在。"] = "That material lot number already exists.",
        ["物料编码不能为空。"] = "The material code is required.",
        ["投料批次不存在。"] = "The input lot does not exist.",
        ["拆分后的批次号不能为空。"] = "The lot number after splitting is required.",
        ["拆分后的批次号已存在。"] = "That split lot number already exists.",
        ["拆分数量必须大于 0。"] = "The split quantity must be greater than 0.",
        ["拆分数量不能超过母批剩余量。"] = "The split quantity cannot exceed the parent lot's remaining amount.",

        // ---- 报警与通用 ----
        ["报警不存在。"] = "Alarm not found.",
        ["数据已被其他操作更新，请刷新后重试。"] = "The data was changed by another operation; refresh and try again.",
        ["该编号已存在或与其他记录冲突，请刷新后重试。"] =
            "That identifier already exists or conflicts with another record; refresh and try again.",
        ["服务器内部错误。"] = "Internal server error."
    };
}
