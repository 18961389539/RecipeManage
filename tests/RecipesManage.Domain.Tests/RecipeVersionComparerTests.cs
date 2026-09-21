using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class RecipeVersionComparerTests
{
    [Fact]
    public void Compare_ReportsSetpointAndAddedStep()
    {
        var engineer = Guid.NewGuid();
        var recipe = MasterRecipe.Create("AL-T", "test", "P", "product", null, engineer);
        var v1 = recipe.RequireDraft();
        var s1 = new RecipeStep(v1.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 60, null,
            [new RecipeParameter(0, "目标温度", "℃", 530, 520, 540, true, true)]);
        var s2 = new RecipeStep(v1.Id, "S20", "hold", StepType.Hold, 1, 100, 0, 60, null,
            [new RecipeParameter(0, "保温温度", "℃", 530, 525, 535, true, true)]);
        v1.ReplaceProcedure([s1, s2], [new RecipeEdge(v1.Id, s1.Id, s2.Id)]);
        Approve(v1, engineer);
        recipe.MarkApproved(v1);

        var next = recipe.CreateNextDraft(engineer, "升温");
        var hold = next.Steps.Single(s => s.Code == "S20");
        var s30 = new RecipeStep(next.Id, "S30", "cool", StepType.Cool, 2, 200, 0, 60, null,
            [new RecipeParameter(0, "终点温度", "℃", 40, 20, 60, true, true)]);
        var newHold = new RecipeStep(next.Id, hold.Code, hold.Name, hold.Type, hold.Ordinal, hold.CanvasX, hold.CanvasY,
            hold.WatchdogSeconds, hold.Description,
            [new RecipeParameter(0, "保温温度", "℃", 533, 525, 535, true, true)]);
        var heat = next.Steps.Single(s => s.Code == "S10");
        next.ReplaceProcedure(
            [heat, newHold, s30],
            [new RecipeEdge(next.Id, heat.Id, newHold.Id), new RecipeEdge(next.Id, newHold.Id, s30.Id)]);

        var diff = RecipeVersionComparer.Compare(v1, next);
        Assert.Equal(1, diff.FromVersion);
        Assert.Equal(2, diff.ToVersion);
        Assert.Contains("S30", diff.AddedSteps);
        Assert.Contains("S30", RecipeVersionComparer.ChangedStepCodes(diff));
        Assert.Contains("S20", RecipeVersionComparer.ChangedStepCodes(diff));
        Assert.Contains(diff.Changes, c => c.Path == "S20.parameters[0].setpoint" && c.Before == "530" && c.After == "533");
        Assert.Contains(diff.Changes, c => c.Path == "changeNote" && c.After == "升温");
        Assert.Contains(diff.Changes, c => c.Path == "edge.add" && c.After == "S20->S30");
    }

    private static void Approve(RecipeVersion version, Guid engineer)
    {
        version.Submit(DateTimeOffset.UtcNow);
        version.Decide(ApprovalLevel.Supervisor, Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        version.Decide(ApprovalLevel.Quality, Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
    }
}
