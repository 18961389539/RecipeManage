using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class BatchLanesTests
{
    [Fact]
    public void Merge_SingleLane_KeepsSimplePhaseName()
    {
        var text = BatchLanes.Merge("WaitingPlcReady", "HT-01", "StepRunning", multiLane: false);
        Assert.Equal("StepRunning", text);
        Assert.Empty(BatchLanes.Parse(text));
    }

    [Fact]
    public void Merge_ParallelLanes_DoesNotOverwritePeer()
    {
        var first = BatchLanes.Merge(null, "HT-01", "StepRunning", multiLane: true);
        var both = BatchLanes.Merge(first, "HT-02", "WaitingPlcReady", multiLane: true);
        var map = BatchLanes.Parse(both);
        Assert.Equal("StepRunning", map["HT-01"]);
        Assert.Equal("WaitingPlcReady", map["HT-02"]);
        Assert.Contains("HT-01:StepRunning", both, StringComparison.Ordinal);
        Assert.Contains("HT-02:WaitingPlcReady", both, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_GroupsByBoundPlc_AndUsesRunningStep()
    {
        var eqA = Guid.NewGuid();
        var eqB = Guid.NewGuid();
        var s10 = Guid.NewGuid();
        var s20 = Guid.NewGuid();
        var s30 = Guid.NewGuid();
        var snapshot = new ControlRecipeSnapshot
        {
            MasterRecipeId = Guid.NewGuid(),
            RecipeVersionId = Guid.NewGuid(),
            VersionNumber = 1,
            RecipeCode = "PAR",
            RecipeName = "parallel",
            ProductCode = "P",
            ProductName = "p",
            FrozenAt = DateTimeOffset.UtcNow,
            UnitEquipment = new Dictionary<string, Guid>
            {
                ["UP-A"] = eqA,
                ["UP-B"] = eqB,
                ["UP-QC"] = eqA
            },
            Steps =
            [
                Step(s10, "S10", "UP-A"),
                Step(s20, "S20", "UP-B"),
                Step(s30, "S30", "UP-QC")
            ]
        };
        var batchId = Guid.NewGuid();
        var execA = new BatchStepExecution(batchId, s10, "S10", "heat", StepType.Heat, 0);
        execA.MarkStarted(DateTimeOffset.UtcNow);
        var execB = new BatchStepExecution(batchId, s20, "S20", "cool", StepType.Cool, 1);
        execB.MarkCompleted(DateTimeOffset.UtcNow, "{}");
        var execQc = new BatchStepExecution(batchId, s30, "S30", "qc", StepType.QualityCheck, 2);
        var events = new List<HandshakeEvent>
        {
            new(batchId, s10, "S10", nameof(HandshakePhase.StepRunning), "phase", "A", null),
            new(batchId, s20, "S20", nameof(HandshakePhase.ReadyToAdvance), "advance", "B", null)
        };

        var lanes = BatchLanes.Build(
            snapshot,
            eqA,
            [execA, execB, execQc],
            events,
            new Dictionary<Guid, string> { [eqA] = "HT-PA", [eqB] = "HT-PB" },
            "HT-PA:StepRunning · HT-PB:ReadyToAdvance");

        Assert.Equal(2, lanes.Count);
        var a = Assert.Single(lanes, l => l.EquipmentCode == "HT-PA");
        Assert.Equal("S10", a.StepCode);
        Assert.Equal("Running", a.Outcome);
        Assert.Equal(nameof(HandshakePhase.StepRunning), a.Phase);
        var b = Assert.Single(lanes, l => l.EquipmentCode == "HT-PB");
        Assert.Equal("S20", b.StepCode);
        Assert.Equal("Completed", b.Outcome);
    }

    [Fact]
    public void IsSkipSafePhase_IncludesAwaitingConfirm()
    {
        Assert.True(BatchLanes.IsSkipSafePhase("HostWait"));
        Assert.True(BatchLanes.IsSkipSafePhase("AwaitingConfirm"));
        Assert.True(BatchLanes.IsSkipSafePhase(nameof(HandshakePhase.WaitingPlcReady)));
        Assert.False(BatchLanes.IsSkipSafePhase(nameof(HandshakePhase.StepRunning)));
        Assert.False(BatchLanes.IsSkipSafePhase(nameof(HandshakePhase.WritingParameters)));
    }

    private static SnapshotStep Step(Guid id, string code, string unit) => new()
    {
        StepId = id,
        Code = code,
        Name = code,
        Type = StepType.Heat,
        Ordinal = 0,
        WatchdogSeconds = 30,
        UnitProcedure = unit,
        Parameters = []
    };
}
