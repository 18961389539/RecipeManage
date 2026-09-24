using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class RecipeVersioningTests
{
    [Fact]
    public void CreateNextDraft_ClonesGraphWithNewIds()
    {
        var engineer = Guid.NewGuid();
        var recipe = MasterRecipe.Create("AL-T", "test", "P", "product", null, engineer);
        var v1 = recipe.RequireDraft();
        var s1 = new RecipeStep(v1.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 60, null,
            [new RecipeParameter(0, "temp", "℃", 530, 520, 540, true, true)]);
        var s2 = new RecipeStep(v1.Id, "S20", "hold", StepType.Hold, 1, 100, 0, 60, null,
            [new RecipeParameter(0, "time", "s", 8, 1, 100, true, true)]);
        v1.ReplaceProcedure([s1, s2], [new RecipeEdge(v1.Id, s1.Id, s2.Id)]);
        v1.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard, engineer, "工艺工程师", "提交审核");
        Assert.Contains(v1.Approvals, a => a.Node == ApprovalNode.Submission && a.Decision == ApprovalDecision.Approved);
        Assert.Contains(v1.Approvals, a => a.Node == ApprovalNode.Supervisor && a.Decision == ApprovalDecision.Pending);
        v1.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        v1.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(v1);

        var next = recipe.CreateNextDraft(engineer, "调整保温");

        Assert.Equal(2, next.VersionNumber);
        Assert.Equal(RecipeStatus.Draft, next.Status);
        Assert.Equal(recipe.CurrentDraftVersionId, next.Id);
        Assert.Equal(2, next.Steps.Count);
        Assert.Empty(next.Steps.Select(s => s.Id).Intersect(v1.Steps.Select(s => s.Id)));
        Assert.All(next.Steps, s => Assert.Equal(next.Id, s.RecipeVersionId));
        Assert.All(next.Steps, s => Assert.All(s.Parameters, p => Assert.Equal(s.Id, p.RecipeStepId)));
        Assert.Equal(v1.Steps[0].Parameters[0].Setpoint, next.Steps[0].Parameters[0].Setpoint);
        Assert.Single(next.Edges);
        Assert.Equal(next.Steps[0].Id, next.Edges[0].FromStepId);
        Assert.Equal(next.Steps[1].Id, next.Edges[0].ToStepId);
        Assert.Equal(Isa88.DefaultUnitProcedure, next.Steps[0].UnitProcedure);
        Assert.Equal(Isa88.DefaultOperation(StepType.Heat), next.Steps[0].Operation);
        Assert.Equal(Isa88.DefaultOperation(StepType.Hold), next.Steps[1].Operation);

        var snapshot = ControlRecipeSnapshotFactory.From(recipe, v1, DateTimeOffset.UtcNow);
        Assert.Equal(Isa88.DefaultUnitProcedure, snapshot.Steps[0].UnitProcedure);
        Assert.Equal(Isa88.DefaultOperation(StepType.Heat), snapshot.Steps[0].Operation);
        Assert.Equal(Isa88.DefaultOperation(StepType.Hold), snapshot.Steps[1].Operation);
    }

    [Fact]
    public void Create_RejectsEncodingLostNames()
    {
        // 开发库里真有一条名称为 ??????? 的配方：客户端把 UTF-8 编进非 Unicode 代码页，
        // 每个汉字变成一个 ?，落库后不可逆，还会随快照冻结进批次与电子批记录。
        var lost = Assert.Throws<DomainException>(() =>
            MasterRecipe.Create("RV-1", "???????", "AL-6061", "?????", null, Guid.NewGuid()));
        Assert.Equal("TEXT_ENCODING_LOSS", lost.Code);

        // 正常中文名不能被误伤。
        var ok = MasterRecipe.Create("AL-HT", "Al-6061-T6 热处理", "AL6061", "铝合金 6061 锻件", null, Guid.NewGuid());
        Assert.Equal("Al-6061-T6 热处理", ok.Name);
    }

    [Fact]
    public void Create_AllowsSingleQuestionMark()
    {
        // 单个 ? 放过：可能是真的在提问。判定只认"整串问号"和"连续两个以上"。
        var recipe = MasterRecipe.Create("AL-OK", "是否需要保温?", "AL6061", "锻件", null, Guid.NewGuid());
        Assert.Equal("是否需要保温?", recipe.Name);
    }

    [Fact]
    public void ReplaceProcedure_RejectsEncodingLostStepNames()
    {
        var recipe = MasterRecipe.Create("AL-T", "test", "P", "product", null, Guid.NewGuid());
        var version = recipe.RequireDraft();
        var step = new RecipeStep(version.Id, "S10", "??", StepType.Heat, 0, 0, 0, 60, null,
            [new RecipeParameter(0, "temp", "℃", 530, 520, 540, true, true)]);

        var error = Assert.Throws<DomainException>(() => version.ReplaceProcedure([step], []));
        Assert.Equal("TEXT_ENCODING_LOSS", error.Code);
    }

    [Fact]
    public void ReplaceProcedure_RejectsDuplicateStepCode()
    {
        var recipe = MasterRecipe.Create("AL-T", "test", "P", "product", null, Guid.NewGuid());
        var version = recipe.RequireDraft();
        var s1 = new RecipeStep(version.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 60, null,
            [new RecipeParameter(0, "temp", "℃", 530, 520, 540, true, true)]);
        // 大小写不同也算重码：Code 是快照 ↔ 执行记录 ↔ 漂移比对的连接键，
        // 重码不会立刻报错，而是让该配方的所有批次详情与电子批记录永久 500。
        var s2 = new RecipeStep(version.Id, "s10", "hold", StepType.Hold, 1, 100, 0, 60, null,
            [new RecipeParameter(0, "time", "s", 8, 1, 100, true, true)]);

        var error = Assert.Throws<DomainException>(() => version.ReplaceProcedure([s1, s2], []));
        Assert.Equal("DUP_STEP_CODE", error.Code);
    }

    [Fact]
    public void RecipeStep_PreservesCustomIsa88OnClone()
    {
        var engineer = Guid.NewGuid();
        var recipe = MasterRecipe.Create("AL-T", "test", "P", "product", null, engineer);
        var v1 = recipe.RequireDraft();
        var s1 = new RecipeStep(v1.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 60, null,
            [new RecipeParameter(0, "temp", "℃", 530, 520, 540, true, true)],
            unitProcedure: "UP-02 时效炉", operation: "OP-Heat 固溶升温");
        v1.ReplaceProcedure([s1], []);
        v1.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
        v1.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        v1.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(v1);

        var next = recipe.CreateNextDraft(engineer, "克隆 ISA-88");
        Assert.Equal("UP-02 时效炉", next.Steps[0].UnitProcedure);
        Assert.Equal("OP-Heat 固溶升温", next.Steps[0].Operation);
    }
}
