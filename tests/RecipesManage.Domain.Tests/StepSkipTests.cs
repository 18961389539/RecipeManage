using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

/// <summary>
/// 跳步规则的判定矩阵。以前这些分支只能起整套服务 + 数据库去测（而且只测到两三个），
/// 现在规则是纯函数，每个拒绝码、每条放行路径都直接钉住。
/// 安全方向是 fail-closed：在跑的批次只在 PLC_Ready / 等待 / 人工确认相位允许跳，没有车道行一律拒绝。
/// </summary>
public sealed class StepSkipTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(ProductionBatch Batch, ControlRecipeSnapshot Snapshot, Guid[] StepIds, Guid EquipmentId)
    {
        public BatchStepExecution Exec(int i) => Batch.StepExecutions.Single(e => e.StepId == StepIds[i]);
        public SnapshotStep Step(int i) => Snapshot.Steps[i];
    }

    /// <summary>三步批次（S10/S20/S30），状态停在 Created，调用方自己推到想要的状态。</summary>
    private static Fixture NewBatch()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var snapshot = new ControlRecipeSnapshot
        {
            MasterRecipeId = Guid.NewGuid(),
            RecipeVersionId = Guid.NewGuid(),
            RecipeCode = "SKIP",
            RecipeName = "skip",
            ProductCode = "P",
            ProductName = "part",
            FrozenAt = Now,
            Steps = ids.Select((id, i) => new SnapshotStep
            {
                StepId = id, Code = $"S{(i + 1) * 10}", Name = $"step{i}", Type = StepType.Heat,
                Ordinal = i, WatchdogSeconds = 60,
            }).ToList(),
            Edges = [],
        };
        var equipmentId = Guid.NewGuid();
        var batch = ProductionBatch.Create("BSKIP", equipmentId, snapshot, "{}", Guid.NewGuid());
        for (var i = 0; i < ids.Length; i++)
            batch.StepExecutions.Add(new BatchStepExecution(batch.Id, ids[i], $"S{(i + 1) * 10}", $"step{i}", StepType.Heat, i));
        batch.AdvanceTo(ids[0], 0);
        return new Fixture(batch, snapshot, ids, equipmentId);
    }

    private static Fixture Running(int runningStep = 0)
    {
        var f = NewBatch();
        f.Batch.Queue();
        f.Batch.MarkRunning(Now);
        f.Exec(runningStep).MarkStarted(Now);
        f.Batch.AdvanceTo(f.StepIds[runningStep], runningStep);
        return f;
    }

    private static Fixture Held(int heldStep = 0)
    {
        var f = Running(heldStep);
        f.Batch.Hold("test");
        return f;
    }

    private static BatchLane Lane(Fixture f, string phase)
    {
        var lane = new BatchLane(f.Batch.Id, f.EquipmentId, "HT-01", "UP");
        lane.Update(phase, StepOutcome.Running);
        return lane;
    }

    private static DomainException Refused(Action act) => Assert.Throws<DomainException>(act);

    // ───────────────────────── ResolveTarget ─────────────────────────

    [Fact]
    public void ResolveTarget_ExplicitStep_Wins()
    {
        var f = Running(0);
        Assert.Equal(f.StepIds[2], StepSkip.ResolveTarget(f.Batch, f.Snapshot, f.StepIds[2]).StepId);
    }

    [Fact]
    public void ResolveTarget_ExplicitStepOutsideSnapshot_IsRefused()
    {
        var f = Running();
        Assert.Equal("SKIP_STEP", Refused(() => StepSkip.ResolveTarget(f.Batch, f.Snapshot, Guid.NewGuid())).Code);
    }

    [Fact]
    public void ResolveTarget_WithoutExplicitStep_UsesTheBatchCurrentStep()
    {
        var f = Running(1);
        Assert.Equal(f.StepIds[1], StepSkip.ResolveTarget(f.Batch, f.Snapshot, null).StepId);
    }

    [Fact]
    public void ResolveTarget_NoCurrentStep_FallsBackToTheRunningExecution()
    {
        var f = Running(2);
        f.Batch.AdvanceTo(null, 0);   // 当前工步指针丢了，但执行记录里有一个在跑
        Assert.Equal(f.StepIds[2], StepSkip.ResolveTarget(f.Batch, f.Snapshot, null).StepId);
    }

    [Fact]
    public void ResolveTarget_NothingRunning_FallsBackToTheClampedIndex()
    {
        var f = NewBatch();
        f.Batch.AdvanceTo(null, 99);   // 越界索引被夹到最后一步
        Assert.Equal(f.StepIds[2], StepSkip.ResolveTarget(f.Batch, f.Snapshot, null).StepId);
        f.Batch.AdvanceTo(null, -5);
        Assert.Equal(f.StepIds[0], StepSkip.ResolveTarget(f.Batch, f.Snapshot, null).StepId);
    }

    // ───────────────────────── Decide：批次在跑 ─────────────────────────

    [Theory]
    [InlineData(nameof(HandshakePhase.WaitingPlcReady))]
    [InlineData(HandshakeView.Held)]
    [InlineData(HandshakeView.AwaitingConfirm)]
    [InlineData(HandshakeView.HostWait)]
    public void Running_SafePhase_ForwardsToTheEngine(string phase)
    {
        var f = Running();
        Assert.Equal(SkipMode.ForwardToEngine, StepSkip.Decide(f.Batch, f.Exec(0), Lane(f, phase)));
    }

    [Theory]
    [InlineData(nameof(HandshakePhase.WritingParameters))]
    [InlineData(nameof(HandshakePhase.AwaitingPlcAck))]
    [InlineData(nameof(HandshakePhase.StepRunning))]
    [InlineData(nameof(HandshakePhase.Completing))]
    [InlineData(nameof(HandshakePhase.ReadyToAdvance))]
    public void Running_UnsafePhase_IsRefusedAndNamesThePhase(string phase)
    {
        var f = Running();
        var ex = Refused(() => StepSkip.Decide(f.Batch, f.Exec(0), Lane(f, phase)));
        Assert.Equal("SKIP_UNSAFE", ex.Code);
        Assert.Contains(phase, ex.Message, StringComparison.Ordinal);
        Assert.Contains("HT-01", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Running_WithoutLaneRow_FailsClosed()
    {
        var f = Running();
        var ex = Refused(() => StepSkip.Decide(f.Batch, f.Exec(0), null));
        Assert.Equal("SKIP_UNSAFE", ex.Code);
    }

    [Fact]
    public void Running_TargetStepNotStartedYet_IsRefused_NotForwardedToThePlc()
    {
        var f = Running(0);   // 在跑的是第 1 步；想跳第 3 步（还没开始）
        var ex = Refused(() => StepSkip.Decide(f.Batch, f.Exec(2), Lane(f, nameof(HandshakePhase.WaitingPlcReady))));
        Assert.Equal("SKIP_UNSAFE", ex.Code);
    }

    [Fact]
    public void Running_AlreadyCompletedOrSkippedStep_IsRefusedAsDone()
    {
        var f = Running(0);
        f.Exec(0).MarkCompleted(Now, "{}");
        Assert.Equal("SKIP_DONE", Refused(() => StepSkip.Decide(f.Batch, f.Exec(0), Lane(f, HandshakeView.Held))).Code);

        var g = Running(0);
        g.Exec(0).MarkSkipped("x");
        Assert.Equal("SKIP_DONE", Refused(() => StepSkip.Decide(g.Batch, g.Exec(0), Lane(g, HandshakeView.Held))).Code);
    }

    // ───────────────────────── Decide：批次已保持 / 故障 ─────────────────────────

    [Theory]
    [InlineData(StepOutcome.Pending)]
    [InlineData(StepOutcome.Running)]
    [InlineData(StepOutcome.Held)]
    [InlineData(StepOutcome.Faulted)]
    public void Held_AppliesOffline_WithoutNeedingALaneRow(StepOutcome outcome)
    {
        var f = Held();   // 第 1 步已开始（Running），第 2、3 步还是 Pending
        var target = f.Exec(0);
        switch (outcome)
        {
            case StepOutcome.Pending: target = f.Exec(1); break;
            case StepOutcome.Held: target.MarkHeld(); break;
            case StepOutcome.Faulted: target.MarkFaulted("x"); break;
        }
        Assert.Equal(outcome, target.Outcome);
        Assert.Equal(SkipMode.ApplyOffline, StepSkip.Decide(f.Batch, target, null));
    }

    [Fact]
    public void Faulted_AppliesOffline()
    {
        var f = Running();
        f.Batch.Fault("PLC", "boom");
        Assert.Equal(SkipMode.ApplyOffline, StepSkip.Decide(f.Batch, f.Exec(0), null));
    }

    [Fact]
    public void Held_CompletedStep_IsRefusedAsDone()
    {
        var f = Held();
        f.Exec(0).MarkCompleted(Now, "{}");
        Assert.Equal("SKIP_DONE", Refused(() => StepSkip.Decide(f.Batch, f.Exec(0), null)).Code);
    }

    [Fact]
    public void BatchNotRunningHeldOrFaulted_CannotSkip()
    {
        var created = NewBatch();
        Assert.Equal("CANNOT_SKIP", Refused(() => StepSkip.Decide(created.Batch, created.Exec(0), null)).Code);

        var queued = NewBatch();
        queued.Batch.Queue();
        Assert.Equal("CANNOT_SKIP", Refused(() => StepSkip.Decide(queued.Batch, queued.Exec(0), null)).Code);

        var aborted = Running();
        aborted.Batch.Abort("x");
        Assert.Equal("CANNOT_SKIP", Refused(() => StepSkip.Decide(aborted.Batch, aborted.Exec(0), null)).Code);
    }

    // ───────────────────────── ApplyOffline ─────────────────────────

    [Fact]
    public void ApplyOffline_MiddleStep_AdvancesToTheNextUnfinishedStepAndRequeues()
    {
        var f = Held(0);
        f.Exec(0).MarkCompleted(Now, "{}");
        f.Exec(1).MarkStarted(Now);
        f.Batch.AdvanceTo(f.StepIds[1], 1);

        var finished = StepSkip.ApplyOffline(f.Batch, f.Snapshot, f.Exec(1), "跳过第二步", Now);

        Assert.False(finished);
        Assert.Equal(StepOutcome.Skipped, f.Exec(1).Outcome);
        Assert.Equal("跳过第二步", f.Exec(1).QualityJson);
        Assert.Equal(f.StepIds[2], f.Batch.CurrentStepId);
        Assert.Equal(2, f.Batch.CurrentStepIndex);
        Assert.Equal(BatchStatus.Queued, f.Batch.Status);
    }

    [Fact]
    public void ApplyOffline_LastRemainingStep_CompletesTheBatch()
    {
        var f = Held(2);
        f.Exec(0).MarkCompleted(Now, "{}");
        f.Exec(1).MarkSkipped("earlier");

        var finished = StepSkip.ApplyOffline(f.Batch, f.Snapshot, f.Exec(2), "最后一步", Now);

        Assert.True(finished);
        Assert.Equal(BatchStatus.Completed, f.Batch.Status);
        Assert.Null(f.Batch.CurrentStepId);
        Assert.Equal(Now, f.Batch.CompletedAt);
    }

    [Fact]
    public void ApplyOffline_FaultedStepSkipped_ClearsTheFaultByRequeueing()
    {
        var f = Running(0);
        f.Exec(0).MarkFaulted("握手超时");
        f.Batch.Fault("PLC_TIMEOUT", "握手超时");

        var finished = StepSkip.ApplyOffline(f.Batch, f.Snapshot, f.Exec(0), "跳过故障步", Now);

        Assert.False(finished);
        Assert.Equal(BatchStatus.Queued, f.Batch.Status);
        Assert.Null(f.Batch.FaultCode);
        Assert.Equal(f.StepIds[1], f.Batch.CurrentStepId);
    }
}
