using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class EquipmentOccupancyTests
{
    [Fact]
    public void Index_PrefersHeldOverOlderFaultedLease()
    {
        var furnace = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        var faulted = Live("BFAULT", furnace, t0);
        faulted.Fault("PLC", "超时");
        var held = Live("BHELD", furnace, t0.AddHours(1));
        held.Hold("现场");

        var lease = new EquipmentLease(furnace, "HT-01", faulted.Id, faulted.BatchNo, t0);
        var map = EquipmentOccupancy.Index(
            [faulted, held],
            b => [b.EquipmentId],
            [lease]);

        var occupant = Assert.Single(map);
        Assert.Equal(furnace, occupant.Key);
        Assert.Equal("BHELD", occupant.Value.BatchNo);
        Assert.Equal(nameof(BatchStatus.Held), occupant.Value.Status);
    }

    [Fact]
    public void Index_FaultedOccupiesWhenAlone()
    {
        var furnace = Guid.NewGuid();
        var faulted = Live("BFAULT", furnace, DateTimeOffset.UtcNow);
        faulted.Fault("PLC", "超时");

        var map = EquipmentOccupancy.Index([faulted], b => [b.EquipmentId]);
        var occupant = Assert.Single(map).Value;
        Assert.Equal("BFAULT", occupant.BatchNo);
        Assert.Equal(nameof(BatchStatus.Faulted), occupant.Status);
    }

    private static ProductionBatch Live(string no, Guid equipmentId, DateTimeOffset started)
    {
        var stepId = Guid.NewGuid();
        var snapshot = new ControlRecipeSnapshot
        {
            MasterRecipeId = Guid.NewGuid(),
            RecipeVersionId = Guid.NewGuid(),
            VersionNumber = 1,
            RecipeCode = "T",
            RecipeName = "t",
            ProductCode = "P",
            ProductName = "p",
            FrozenAt = started,
            Steps =
            [
                new SnapshotStep
                {
                    StepId = stepId,
                    Code = "S10",
                    Name = "heat",
                    Type = StepType.Heat,
                    Ordinal = 0,
                    WatchdogSeconds = 30,
                    Parameters = []
                }
            ]
        };
        var batch = ProductionBatch.Create(no, equipmentId, snapshot, "{}", Guid.NewGuid());
        batch.StepExecutions.Add(new BatchStepExecution(batch.Id, stepId, "S10", "heat", StepType.Heat, 0));
        batch.Queue();
        batch.MarkRunning(started);
        return batch;
    }
}
