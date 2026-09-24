using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class BatchScaleTests
{
    [Fact]
    public void ScaleFactor_MultipliesOnlyMarkedParameters()
    {
        var engineer = Guid.NewGuid();
        var recipe = MasterRecipe.Create("SC-1", "scale", "P", "part", null, engineer);
        var draft = recipe.RequireDraft();
        var step = new RecipeStep(draft.Id, "S10", "charge", StepType.Heat, 0, 0, 0, 30, null,
        [
            new RecipeParameter(0, "目标温度", "℃", 530, 520, 540, true, true),
            new RecipeParameter(1, "装炉量", "kg", 100, 50, 200, true, false, scaleWithBatch: true)
        ]);
        draft.ReplaceProcedure([step], []);
        draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard, engineer, "eng", null);
        draft.Decide(Guid.NewGuid(), "s", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(Guid.NewGuid(), "q", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);

        var snapshot = ControlRecipeSnapshotFactory.From(recipe, draft, DateTimeOffset.UtcNow, 2, "LOT-A");
        Assert.Equal(2, snapshot.ScaleFactor);
        Assert.Equal("LOT-A", snapshot.LotNumber);
        Assert.Equal(530, snapshot.Steps[0].Parameters[0].Setpoint);
        Assert.Equal(520, snapshot.Steps[0].Parameters[0].Min);
        Assert.Equal(200, snapshot.Steps[0].Parameters[1].Setpoint);
        Assert.Equal(100, snapshot.Steps[0].Parameters[1].Min);
        Assert.Equal(400, snapshot.Steps[0].Parameters[1].Max);
        Assert.True(snapshot.Steps[0].Parameters[1].ScaleWithBatch);
    }

    [Fact]
    public void ScaleFactor_One_OmitsMetadata()
    {
        var engineer = Guid.NewGuid();
        var recipe = MasterRecipe.Create("SC-2", "scale", "P", "part", null, engineer);
        var draft = recipe.RequireDraft();
        var step = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
            [new RecipeParameter(0, "temp", "℃", 100, 90, 110, true, true)]);
        draft.ReplaceProcedure([step], []);
        draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
        draft.Decide(Guid.NewGuid(), "s", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(Guid.NewGuid(), "q", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);

        var snapshot = ControlRecipeSnapshotFactory.From(recipe, draft, DateTimeOffset.UtcNow);
        Assert.Null(snapshot.ScaleFactor);
        Assert.Null(snapshot.LotNumber);
    }

    [Fact]
    public void RejectsNonPositiveScale()
    {
        var engineer = Guid.NewGuid();
        var recipe = MasterRecipe.Create("SC-3", "scale", "P", "part", null, engineer);
        var draft = recipe.RequireDraft();
        var step = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
            [new RecipeParameter(0, "temp", "℃", 100, null, null, true, false)]);
        draft.ReplaceProcedure([step], []);
        draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
        draft.Decide(Guid.NewGuid(), "s", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(Guid.NewGuid(), "q", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);

        var ex = Assert.Throws<RecipesManage.Domain.Common.DomainException>(() =>
            ControlRecipeSnapshotFactory.From(recipe, draft, DateTimeOffset.UtcNow, 0));
        Assert.Equal("SCALE", ex.Code);
    }
}
