using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class BatchLanesTests
{
    [Fact]
    public void Format_SingleLane_KeepsSimplePhaseName()
    {
        var text = BatchLanes.Format([Lane("HT-01", nameof(HandshakePhase.StepRunning))], "WaitingPlcReady");
        Assert.Equal("StepRunning", text);
        // 单车道不带 "CODE:" 前缀：老批次与 UI 都直接把这个值当相位读。
        Assert.Equal("StepRunning", HandshakeView.Token(text));
    }

    [Fact]
    public void Format_NoLanes_KeepsCurrentValue()
    {
        Assert.Equal("HostWait", BatchLanes.Format([], "HostWait"));
    }

    [Fact]
    public void Format_ParallelLanes_IsOrderedAndReadableByTheDisplayLayer()
    {
        // 展示层（标题条 / 四步进度）仍要能解析拼串，所以格式必须稳定、按设备码有序。
        // 但**决策路径不许解析**：跳步安全门只认 batch_lanes 行，见 SkipPhaseGateTests。
        var rows = new List<BatchLane>
        {
            Lane("HT-02", nameof(HandshakePhase.WaitingPlcReady)),
            Lane("HT-01", nameof(HandshakePhase.StepRunning))
        };
        var text = BatchLanes.Format(rows, "WaitingPlcReady");

        Assert.Equal("HT-01:StepRunning · HT-02:WaitingPlcReady", text);
        var map = HandshakeView.Parse(text);
        Assert.Equal(2, map.Count);
        Assert.Equal(nameof(HandshakePhase.StepRunning), map["HT-01"]);
        Assert.Equal(nameof(HandshakePhase.WaitingPlcReady), map["HT-02"]);
        Assert.Equal(HandshakeView.Separator, " · ");
    }

    private static BatchLane Lane(string code, string phase)
    {
        var lane = new BatchLane(Guid.NewGuid(), Guid.NewGuid(), code, "UP-A");
        lane.Update(phase, StepOutcome.Running);
        return lane;
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
            new Dictionary<Guid, string> { [eqA] = "HT-PA", [eqB] = "HT-PB" });

        Assert.Equal(2, lanes.Count);
        var a = Assert.Single(lanes, l => l.EquipmentCode == "HT-PA");
        Assert.Equal("S10", a.StepCode);
        Assert.Equal(StepOutcome.Running, a.Outcome);
        Assert.Equal(nameof(HandshakePhase.StepRunning), a.Phase);
        var b = Assert.Single(lanes, l => l.EquipmentCode == "HT-PB");
        Assert.Equal("S20", b.StepCode);
        Assert.Equal(StepOutcome.Completed, b.Outcome);
        Assert.Equal(nameof(HandshakePhase.ReadyToAdvance), b.Phase);
    }

    [Fact]
    public void Build_CompletedLane_DoesNotCopyOutcomeIntoPhase()
    {
        var eqA = Guid.NewGuid();
        var s10 = Guid.NewGuid();
        var snapshot = new ControlRecipeSnapshot
        {
            MasterRecipeId = Guid.NewGuid(),
            RecipeVersionId = Guid.NewGuid(),
            VersionNumber = 1,
            RecipeCode = "ONE",
            RecipeName = "one",
            ProductCode = "P",
            ProductName = "p",
            FrozenAt = DateTimeOffset.UtcNow,
            Steps = [Step(s10, "S10", "UP-A")]
        };
        var batchId = Guid.NewGuid();
        var exec = new BatchStepExecution(batchId, s10, "S10", "heat", StepType.Heat, 0);
        exec.MarkCompleted(DateTimeOffset.UtcNow, "{}");
        var lanes = BatchLanes.Build(
            snapshot,
            eqA,
            [exec],
            [],
            new Dictionary<Guid, string> { [eqA] = "HT-01" });
        var lane = Assert.Single(lanes);
        Assert.Equal(StepOutcome.Completed, lane.Outcome);
        Assert.Equal(nameof(HandshakePhase.ReadyToAdvance), lane.Phase);
    }

    /// <summary>
    /// <c>Build</c> 的签名里已经没有批次展示串这个参数：车道当前相位的真源只有 batch_lanes 行，
    /// 展示串是 <c>Format</c> 的输出。这条是编译期保证，写在这儿是为了说明为什么少一个参数。
    /// </summary>
    [Fact]
    public void Build_WithoutEvents_DerivesPhaseFromStepOutcome()
    {
        var eqA = Guid.NewGuid();
        var s10 = Guid.NewGuid();
        var snapshot = new ControlRecipeSnapshot
        {
            MasterRecipeId = Guid.NewGuid(),
            RecipeVersionId = Guid.NewGuid(),
            VersionNumber = 1,
            RecipeCode = "LEGACY",
            RecipeName = "legacy",
            ProductCode = "P",
            ProductName = "p",
            FrozenAt = DateTimeOffset.UtcNow,
            Steps = [Step(s10, "S10", "UP-A")]
        };
        var pending = new BatchStepExecution(Guid.NewGuid(), s10, "S10", "heat", StepType.Heat, 0);

        var lane = Assert.Single(BatchLanes.Build(
            snapshot, eqA, [pending], [], new Dictionary<Guid, string> { [eqA] = "HT-01" }));

        Assert.Equal(StepOutcome.Pending, lane.Outcome);
        Assert.Equal(nameof(HandshakePhase.WaitingPlcReady), lane.Phase);
    }

    /// <summary>历史事件里存过展示串（早年的写法），重建时不能被当成相位用。</summary>
    [Fact]
    public void Build_IgnoresDisplayStringStoredInLegacyEvent()
    {
        var eqA = Guid.NewGuid();
        var s10 = Guid.NewGuid();
        var snapshot = new ControlRecipeSnapshot
        {
            MasterRecipeId = Guid.NewGuid(),
            RecipeVersionId = Guid.NewGuid(),
            VersionNumber = 1,
            RecipeCode = "JUNK",
            RecipeName = "junk",
            ProductCode = "P",
            ProductName = "p",
            FrozenAt = DateTimeOffset.UtcNow,
            Steps = [Step(s10, "S10", "UP-A")]
        };
        var batchId = Guid.NewGuid();
        var completed = new BatchStepExecution(batchId, s10, "S10", "heat", StepType.Heat, 0);
        completed.MarkCompleted(DateTimeOffset.UtcNow, "{}");
        var junkEvent = new HandshakeEvent(
            batchId, s10, "S10", "HT-01:StepRunning · HT-02:Held", "skip", "早年写的展示串", null);

        var lane = Assert.Single(BatchLanes.Build(
            snapshot, eqA, [completed], [junkEvent], new Dictionary<Guid, string> { [eqA] = "HT-01" }));

        Assert.Equal(nameof(HandshakePhase.ReadyToAdvance), lane.Phase);
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
