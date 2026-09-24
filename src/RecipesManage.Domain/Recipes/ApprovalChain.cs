using System.Text.Json;
using System.Text.Json.Serialization;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Domain.Recipes;

/// <summary>
/// 审批节点的稳定标识。
///
/// 只用来标记历史行、给默认文案与节点名兜底，**不参与任何授权判定**——
/// 谁能签、签了是什么意思，一律看 <see cref="ApprovalRecord"/> 上冻结下来的副本。
/// 因此新增节点不必再改这个枚举，改的是链配置。
/// </summary>
public enum ApprovalNode
{
    Submission = 1,
    Supervisor = 2,
    Quality = 3,
    Release = 4
}

/// <summary>
/// 链上的一个节点定义。管理员可改名称、角色与两种签名含义；提交时整条链会**原样冻结**进审批记录，
/// 之后再改配置也不会追溯性改变别人已经签下的含义（与控制配方快照同一套纪律）。
/// </summary>
public sealed record ApprovalChainStep(
    ApprovalNode Node,
    string Title,
    UserRole RequiredRole,
    string MeaningApproved,
    string MeaningRejected)
{
    /// <summary>节点未决时展示的待签提示。由节点名派生，避免配置面比实际需要的更大。不落库。</summary>
    [JsonIgnore]
    public string PendingMeaning => Prompt(Title);

    public string MeaningFor(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Approved => MeaningApproved,
        ApprovalDecision.Rejected => MeaningRejected,
        _ => PendingMeaning
    };

    /// <summary>待签提示的唯一算法。审批记录冻结的是名称与两种含义，未决时的提示由这里现推。</summary>
    public static string Prompt(string title) => $"待{title}签署。";

    public static ApprovalChainStep Of(ApprovalNode node, UserRole role, string title, string approved, string rejected) =>
        new(node, title.Trim(), role, approved.Trim(), rejected.Trim());
}

/// <summary>
/// 一条命名审批链。<see cref="Steps"/> 就是审核节点，不含提交动作；
/// 持久化为 approval_chains.StepsJson，因此 <see cref="Validate"/> 就是保存链时的校验规则。
/// </summary>
public sealed record ApprovalChain(string Code, string Name, IReadOnlyList<ApprovalChainStep> Steps)
{
    /// <summary>提交人自己那一签。它是提交动作的凭据，不是可配置的审核节点，所以不进链、也不可被 Decide 推进。</summary>
    public const string SubmissionTitle = "提交人";

    public static readonly ApprovalChainStep Submission = ApprovalChainStep.Of(
        ApprovalNode.Submission,
        UserRole.ProcessEngineer,
        SubmissionTitle,
        "我作为工艺工程师确认本版本 Procedure / Steps 与 Parameters / Setpoints 准确，提交多级审核。",
        "工艺工程师提交审核。");

    /// <summary>现行三级链的原样复刻，也是库里的默认链。</summary>
    public static readonly ApprovalChain Standard = new(
        "standard", "标准三级",
        [
            ApprovalChainStep.Of(
                ApprovalNode.Supervisor, UserRole.Supervisor, "工艺主管",
                "我作为工艺主管确认工艺路径可执行，批准进入质量审核。",
                "我作为工艺主管驳回：工艺路径不可执行或需要返工。"),
            ApprovalChainStep.Of(
                ApprovalNode.Quality, UserRole.Quality, "质量审核",
                "我作为质量审核人确认参数窗口可接受，批准本版本作为生效主配方。",
                "我作为质量审核人驳回：参数窗口不可接受或需要返工。")
        ]);

    /// <summary>
    /// 校验一条链能不能保存。返回可读原因，空串表示通过。
    /// 角色不得重复：这正是"这一版还差谁签"必须无歧义的前提，也是历史索引 (VersionId, Seq)
    /// 能成立的底气——同一角色签两次的话，两个节点就没有可区分的身份了。
    /// </summary>
    public static string? Validate(IReadOnlyList<ApprovalChainStep> steps)
    {
        if (steps is null || steps.Count == 0)
            return "审批链至少需要一个审核节点。";
        if (steps.Count > 8)
            return "审批链最多 8 个审核节点。";

        foreach (var group in steps.GroupBy(s => s.RequiredRole))
            if (group.Count() > 1)
                return $"角色 {group.Key} 在这条链上出现多次；同一版本同一角色只签一次。";
        if (steps.Any(s => s.RequiredRole is UserRole.ProcessEngineer or UserRole.Operator))
            return "审核节点不能要求提交配方（工艺）或操作设备的角色，职责会自相冲突。";
        if (steps.Any(s => s.Node == ApprovalNode.Submission))
            return $"「{SubmissionTitle}」是提交动作，不能作为审核节点。";

        foreach (var step in steps)
        {
            if (Blank(step.Title)) return "节点名称不能为空。";
            if (Blank(step.MeaningApproved)) return $"节点「{step.Title}」缺少通过时的签名含义。";
            if (Blank(step.MeaningRejected)) return $"节点「{step.Title}」缺少驳回时的签名含义。";
        }

        return null;
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}

/// <summary>历史行与测试构造只用节点标识即可；链上配的名称与含义优先于这里的缺省值。</summary>
public static class ApprovalNodeExtensions
{
    public static string ToTitle(this ApprovalNode node) => node switch
    {
        ApprovalNode.Submission => ApprovalChain.SubmissionTitle,
        ApprovalNode.Supervisor => "工艺主管",
        ApprovalNode.Quality => "质量审核",
        ApprovalNode.Release => "出厂放行",
        _ => node.ToString()
    };

    public static UserRole ToRequiredRole(this ApprovalNode node) => node switch
    {
        ApprovalNode.Submission => UserRole.ProcessEngineer,
        ApprovalNode.Supervisor => UserRole.Supervisor,
        ApprovalNode.Quality => UserRole.Quality,
        ApprovalNode.Release => UserRole.Supervisor,
        _ => throw new DomainException("APPROVAL_NODE", $"节点 {node} 没有缺省审核角色。")
    };

    public static string ToDefaultMeaning(this ApprovalNode node, ApprovalDecision decision)
    {
        if (node == ApprovalNode.Submission) return ApprovalChain.Submission.MeaningFor(decision);
        var step = ApprovalChain.Standard.Steps.FirstOrDefault(s => s.Node == node);
        if (step is not null) return step.MeaningFor(decision);
        return node.ToTitle() + "签署。";
    }

    /// <summary>
    /// 新建节点时的标识由要求角色派生——管理员只配"叫什么、谁签、签了什么话"，
    /// 不必理解这个内部标识（它只服务历史行回填与缺省文案）。
    /// </summary>
    public static ApprovalNode NodeFor(this UserRole role) => role switch
    {
        UserRole.Supervisor => ApprovalNode.Supervisor,
        UserRole.Quality => ApprovalNode.Quality,
        _ => ApprovalNode.Release
    };
}

/// <summary>
/// 可配置的命名审批链。节点定义整体存 JSON（与 phase_templates.ParametersJson 同一套做法），
/// 因为节点永远整条读写，按行拆表只会多一次 join 和一处能让半条链不一致的地方。
/// </summary>
public sealed class ApprovalChainConfig : Entity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string StepsJson { get; private set; } = "[]";
    /// <summary>配方没选链时走这条。全库最多一条，由服务层保证。</summary>
    public bool IsDefault { get; private set; }
    public bool Enabled { get; private set; } = true;

    private ApprovalChainConfig() { }

    public static ApprovalChainConfig Create(string code, string name, IReadOnlyList<ApprovalChainStep> steps, bool isDefault, bool enabled)
    {
        var config = new ApprovalChainConfig();
        config.Apply(code, name, steps, isDefault, enabled);
        return config;
    }

    public void Apply(string code, string name, IReadOnlyList<ApprovalChainStep> steps, bool isDefault, bool enabled)
    {
        var reason = ApprovalChain.Validate(steps);
        if (reason is not null)
            throw new DomainException("APPROVAL_CHAIN", reason);
        if (string.IsNullOrWhiteSpace(code))
            throw new DomainException("APPROVAL_CHAIN", "审批链编码不能为空。");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("APPROVAL_CHAIN", "审批链名称不能为空。");

        Code = code.Trim();
        Name = name.Trim();
        StepsJson = Write(steps);
        IsDefault = isDefault;
        Enabled = enabled;
        Touch();
    }

    public ApprovalChain ToChain() => new(Code, Name, Read(StepsJson));

    /// <summary>让出默认位。同一时刻只能有一条默认链，所以设新默认必须同时撤旧默认。</summary>
    public void UnsetDefault()
    {
        IsDefault = false;
        Touch();
    }

    /// <summary>
    /// 枚举按名字存（与 phase_templates.ParametersJson 的整数存法不同，这里是有意为之）：
    /// 有人重排枚举时不会静默改变已种下去的链的含义。中文会被转义成 \uXXXX，所以这段只给机器读。
    /// </summary>
    private static readonly JsonSerializerOptions Shape = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Write(IReadOnlyList<ApprovalChainStep> steps) => JsonSerializer.Serialize(steps, Shape);

    /// <summary>坏 JSON 一律抛，不静默回退默认链：回退等于把别人的在审版本换一条链跑。</summary>
    public static IReadOnlyList<ApprovalChainStep> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ApprovalChainStep>>(json, Shape) ?? [];
        }
        catch (JsonException ex)
        {
            throw new DomainException("APPROVAL_CHAIN", $"审批链配置 JSON 无效：{ex.Message}");
        }
    }
}
