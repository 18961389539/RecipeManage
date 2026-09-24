using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

/// <summary>
/// 审批链作为数据的规则。这里的重点不是"能不能配"，而是**配了之后在审版本不受影响**——
/// 那是电子签名的含义：一个人签过的话不能因为管理员事后改了配置而改变。
/// </summary>
public sealed class ApprovalChainTests
{
    private static readonly Guid Engineer = Guid.NewGuid();
    private static readonly Guid Supervisor = Guid.NewGuid();
    private static readonly Guid Quality = Guid.NewGuid();
    private static readonly Guid PlantManager = Guid.NewGuid();

    private static ApprovalChainStep Step(ApprovalNode node, UserRole role, string title) =>
        new(node, title, role, $"我确认「{title}」通过。", $"我驳回「{title}」。");

    private static RecipeVersion DraftWithStep()
    {
        var recipe = MasterRecipe.Create("AC-1", "chain", "P", "part", null, Engineer);
        var draft = recipe.RequireDraft();
        draft.ReplaceProcedure(
            [new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 60, null,
                [new RecipeParameter(0, "temp", "℃", 530, 520, 540, true, true)])],
            []);
        return draft;
    }

    [Fact]
    public void Submit_ExpandsTheWholeChainFrozen_AtSubmissionTime()
    {
        var chain = new ApprovalChain("short", "短链", [Step(ApprovalNode.Quality, UserRole.Quality, "质量放行")]);
        var draft = DraftWithStep();

        draft.Submit(DateTimeOffset.UtcNow, chain, Engineer, "工艺工程师", "提交");

        // 提交动作 + 一个审核节点；不是"签完才开下一级"。
        Assert.Equal([0, 1], draft.Approvals.OrderBy(a => a.Seq).Select(a => a.Seq));
        var node = draft.Approvals.Single(a => a.Seq == 1);
        Assert.Equal("质量放行", node.Title);
        Assert.Equal(UserRole.Quality, node.RequiredRole);
        Assert.Equal("待质量放行签署。", node.Meaning);
        Assert.Equal(RecipeStatus.InReview, draft.Status);
    }

    [Fact]
    public void ChangingTheChainAfterSubmit_DoesNotTouchTheInFlightVersion()
    {
        var chain = new ApprovalChain("long", "长链",
            [Step(ApprovalNode.Supervisor, UserRole.Supervisor, "工艺主管"),
             Step(ApprovalNode.Quality, UserRole.Quality, "质量审核"),
             Step(ApprovalNode.Release, UserRole.Admin, "厂长放行")]);
        var draft = DraftWithStep();
        draft.Submit(DateTimeOffset.UtcNow, chain, Engineer, "工艺工程师", "提交");

        // 提交之后管理员把链改成只剩一级（甚至换个名字）——在审那一版必须照旧要三签。
        var shortened = new ApprovalChain("long", "短链", [Step(ApprovalNode.Quality, UserRole.Quality, "质量改口")]);
        Assert.Single(shortened.Steps);
        Assert.Equal(3, draft.Approvals.Count(a => a.Decision == ApprovalDecision.Pending));
        Assert.Equal("厂长放行", draft.Approvals.Single(a => a.Seq == 3).Title);

        draft.Decide(Supervisor, "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(Quality, "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        Assert.Equal(RecipeStatus.InReview, draft.Status);
        Assert.Equal("厂长放行", draft.HeadNode!.Title);

        draft.Decide(PlantManager, "厂长", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        Assert.Equal(RecipeStatus.Approved, draft.Status);
        Assert.Null(draft.HeadNode);
        // 提交动作 + 三签，一条不多一条不少。
        Assert.Equal(4, draft.Approvals.Count);
    }

    [Fact]
    public void OnlyTheHeadNodeCanBeDecided_LaterNodesWaitTheirTurn()
    {
        var draft = DraftWithStep();
        draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard, Engineer, "工艺工程师", "提交");

        Assert.Equal(ApprovalNode.Supervisor, draft.HeadNode!.Node);
        draft.Decide(Supervisor, "主管", ApprovalDecision.Approved, "路径可执行", DateTimeOffset.UtcNow);
        Assert.Equal(ApprovalNode.Quality, draft.HeadNode!.Node);
        Assert.Equal(RecipeStatus.InReview, draft.Status);
    }

    [Fact]
    public void SignedMeaningIsTheOneStoredOnTheNode_NotReDerivedFromConfig()
    {
        var chain = new ApprovalChain("c", "链", [Step(ApprovalNode.Supervisor, UserRole.Supervisor, "工艺主管")]);
        var draft = DraftWithStep();
        draft.Submit(DateTimeOffset.UtcNow, chain, Engineer, "工艺工程师", "提交");

        var record = draft.Decide(Supervisor, "主管", ApprovalDecision.Rejected, "返工", DateTimeOffset.UtcNow);

        Assert.Equal("我驳回「工艺主管」。", record.Meaning);
        Assert.Equal("我确认「工艺主管」通过。", record.MeaningApproved);
        // 同一行两个含义都在，所以未决时选哪个结论都签得出对应的原话。
        Assert.Equal(ApprovalDecision.Rejected, record.Decision);
        Assert.Equal(RecipeStatus.Rejected, draft.Status);
    }

    [Fact]
    public void SamePersonCannotFillTwoNodes_EvenAcrossSubmissionAndReview()
    {
        var draft = DraftWithStep();
        draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard, Supervisor, "主管兼报工程师", "提交");

        var ex = Assert.Throws<DomainException>(() =>
            draft.Decide(Supervisor, "主管", ApprovalDecision.Approved, "我自己批", DateTimeOffset.UtcNow));
        Assert.Equal("SEGREGATION_OF_DUTIES", ex.Code);
    }

    [Fact]
    public void Submit_RefusesAChainWithNoReviewNode()
    {
        var draft = DraftWithStep();
        var empty = new ApprovalChain("none", "空链", []);
        var ex = Assert.Throws<DomainException>(() =>
            draft.Submit(DateTimeOffset.UtcNow, empty, Engineer, "工艺工程师", "提交"));
        Assert.Equal("APPROVAL_CHAIN", ex.Code);
    }

    [Fact]
    public void Validate_RejectsDuplicatedRolesAndBlankText()
    {
        Assert.NotNull(ApprovalChain.Validate([]));
        Assert.NotNull(ApprovalChain.Validate(
            [Step(ApprovalNode.Supervisor, UserRole.Quality, "甲"), Step(ApprovalNode.Quality, UserRole.Quality, "乙")]));
        Assert.NotNull(ApprovalChain.Validate(
            [Step(ApprovalNode.Supervisor, UserRole.Supervisor, "甲"), Step(ApprovalNode.Quality, UserRole.Supervisor, "乙")]));
        Assert.NotNull(ApprovalChain.Validate(
            [new ApprovalChainStep(ApprovalNode.Supervisor, " ", UserRole.Supervisor, "通过", "驳回")]));
        Assert.NotNull(ApprovalChain.Validate(
            [new ApprovalChainStep(ApprovalNode.Supervisor, "甲", UserRole.Supervisor, "", "驳回")]));
        // 提交动作不是一个可配的节点。
        Assert.NotNull(ApprovalChain.Validate(
            [Step(ApprovalNode.Submission, UserRole.Supervisor, "提交")]));
        // 提交配方的人与签核的人不能是同一批角色，否则自己批自己。
        Assert.NotNull(ApprovalChain.Validate([Step(ApprovalNode.Supervisor, UserRole.ProcessEngineer, "自审")]));
        Assert.NotNull(ApprovalChain.Validate([Step(ApprovalNode.Supervisor, UserRole.Operator, "操作员签核")]));
        Assert.Null(ApprovalChain.Validate(ApprovalChain.Standard.Steps));
        // 节点标识由角色派生，只是缺省文案的键，所以重复它不构成拒绝项（真正会歧义的是角色重复，上面已拦）。
        Assert.Null(ApprovalChain.Validate(
            [Step(ApprovalNode.Supervisor, UserRole.Supervisor, "初审"), Step(ApprovalNode.Supervisor, UserRole.Quality, "复审")]));
    }

    [Fact]
    public void StepsSurviveTheJsonRoundTrip_EnumByNameNotByNumber()
    {
        var json = ApprovalChainConfig.Write(ApprovalChain.Standard.Steps);

        // 枚举按名字入库：重排枚举不会静默改变已存链的含义（中文文案会被转义成 \uXXXX，
        // 所以这段是给机器读的，不是给人手工编辑的）。
        Assert.Contains("\"Supervisor\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Quality\"", json, StringComparison.Ordinal);

        var back = ApprovalChainConfig.Read(json);
        Assert.Equal(ApprovalChain.Standard.Steps, back);
    }

    [Fact]
    public void Read_BlankJsonIsEmpty_AndMalformedJsonThrows()
    {
        Assert.Empty(ApprovalChainConfig.Read(null));
        Assert.Empty(ApprovalChainConfig.Read(""));
        // 回退成空链会让提交报"没有审核节点"；静默退回缺省链等于悄悄换掉要谁签。
        Assert.Throws<DomainException>(() => ApprovalChainConfig.Read("{ 这不是 JSON"));
    }

    [Fact]
    public void StandardChain_StillIsTheHistoricalSupervisorThenQuality()
    {
        var steps = ApprovalChain.Standard.Steps;
        Assert.Equal(
            [(ApprovalNode.Supervisor, UserRole.Supervisor, "工艺主管"),
             (ApprovalNode.Quality, UserRole.Quality, "质量审核")],
            steps.Select(s => (s.Node, s.RequiredRole, s.Title)));
        Assert.Contains("工艺工程师", ApprovalChain.Submission.MeaningFor(ApprovalDecision.Approved));
        Assert.Equal(ApprovalChain.SubmissionTitle, ApprovalChain.Submission.Title);
    }
}
