using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class ProductionBatchLifecycleTests
{
    [Fact]
    public void Hold_Resume_ThenQueueFromHeld()
    {
        var batch = Sample();
        batch.Queue();
        batch.MarkRunning(DateTimeOffset.UtcNow);
        batch.UpdateHandshake("StepRunning");
        batch.Hold("现场确认");
        Assert.Equal(BatchStatus.Held, batch.Status);
        Assert.Equal("StepRunning", batch.HandshakePhase);
        batch.Resume();
        Assert.Equal(BatchStatus.Queued, batch.Status);
        Assert.Equal("StepRunning", batch.HandshakePhase);
    }

    [Fact]
    public void Hold_WhenAlreadyHeld_IsIdempotent()
    {
        var batch = Sample();
        batch.Queue();
        batch.MarkRunning(DateTimeOffset.UtcNow);
        batch.Hold("第一次");
        batch.Hold("第二次");
        Assert.Equal(BatchStatus.Held, batch.Status);
        Assert.Equal("第一次", batch.FaultMessage);
    }

    [Fact]
    public void SkipMarksExecutionAndDoesNotAllowCompletedSkip()
    {
        var batch = Sample();
        var exec = batch.StepExecutions[0];
        exec.MarkSkipped("主管跳步");
        Assert.Equal(StepOutcome.Skipped, exec.Outcome);
        exec.MarkCompleted(DateTimeOffset.UtcNow, "{}");
        Assert.Equal(StepOutcome.Completed, exec.Outcome);
    }

    [Fact]
    public void CannotHoldCompleted()
    {
        var batch = Sample();
        batch.Queue();
        batch.MarkRunning(DateTimeOffset.UtcNow);
        batch.Complete(DateTimeOffset.UtcNow);
        Assert.Throws<DomainException>(() => batch.Hold("x"));
    }

    [Fact]
    public void QualityRelease_FromCompleted_SealsDisposition()
    {
        var batch = Sample();
        batch.Queue();
        batch.MarkRunning(DateTimeOffset.UtcNow);
        batch.Complete(DateTimeOffset.UtcNow);
        batch.Release("qa", "握手归档合格", DateTimeOffset.UtcNow);
        Assert.Equal(BatchStatus.Released, batch.Status);
        Assert.Equal(nameof(RecipesManage.Domain.Handshake.HandshakePhase.ReadyToAdvance), batch.HandshakePhase);
        Assert.Equal("qa", batch.ReleasedBy);
        Assert.Equal("握手归档合格", batch.ReleaseComment);
        var abort = Assert.Throws<DomainException>(() => batch.Abort("no"));
        Assert.Equal("ALREADY_DONE", abort.Code);
    }

    [Fact]
    public void QualityReject_RequiresComment()
    {
        var batch = Sample();
        batch.Queue();
        batch.MarkRunning(DateTimeOffset.UtcNow);
        batch.Complete(DateTimeOffset.UtcNow);
        var missing = Assert.Throws<DomainException>(() => batch.RejectDisposition("qa", "  ", DateTimeOffset.UtcNow));
        Assert.Equal("REJECT_REASON", missing.Code);
        batch.RejectDisposition("qa", "硬度超差", DateTimeOffset.UtcNow);
        Assert.Equal(BatchStatus.DispositionRejected, batch.Status);
    }

    [Fact]
    public void Fault_CannotOverwriteAnAbortedBatch()
    {
        var batch = Sample();
        batch.Queue();
        batch.MarkRunning(DateTimeOffset.UtcNow);
        batch.Abort("现场安全事件，操作员中止");
        var completedAt = batch.CompletedAt;

        // 引擎异常与中止同 tick 撞车时，写回 Faulted 会把中止原因和时间戳一起抹掉。
        var faulted = Assert.Throws<DomainException>(() => batch.Fault("ENGINE", "握手超时"));
        Assert.Equal("ALREADY_DONE", faulted.Code);
        Assert.Equal(BatchStatus.Aborted, batch.Status);
        Assert.Equal("现场安全事件，操作员中止", batch.FaultMessage);
        Assert.Equal(completedAt, batch.CompletedAt);
    }

    [Fact]
    public void Complete_CannotOverwriteQualityDisposition()
    {
        var batch = Sample();
        batch.Queue();
        batch.MarkRunning(DateTimeOffset.UtcNow);
        batch.Complete(DateTimeOffset.UtcNow);
        batch.Release("qa", "归档合格", DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => batch.Complete(DateTimeOffset.UtcNow));
        Assert.Throws<DomainException>(() => batch.Fault("ENGINE", "迟到的异常"));
        Assert.Equal(BatchStatus.Released, batch.Status);
        Assert.True(batch.IsTerminal);
    }

    [Fact]
    public void Faulted_IsNotTerminal_SoOperatorCanRequeue()
    {
        var batch = Sample();
        batch.Queue();
        batch.MarkRunning(DateTimeOffset.UtcNow);
        batch.Fault("PLC", "握手超时");
        Assert.False(batch.IsTerminal);
        batch.Queue();
        Assert.Equal(BatchStatus.Queued, batch.Status);
    }

    private static ProductionBatch Sample()
    {
        var snapshot = new ControlRecipeSnapshot
        {
            MasterRecipeId = Guid.NewGuid(),
            RecipeVersionId = Guid.NewGuid(),
            VersionNumber = 1,
            RecipeCode = "T",
            RecipeName = "t",
            ProductCode = "P",
            ProductName = "p",
            FrozenAt = DateTimeOffset.UtcNow,
            Steps =
            [
                new SnapshotStep
                {
                    StepId = Guid.NewGuid(),
                    Code = "S10",
                    Name = "heat",
                    Type = StepType.Heat,
                    Ordinal = 0,
                    WatchdogSeconds = 30,
                    Parameters = []
                }
            ]
        };
        var batch = ProductionBatch.Create("B1", Guid.NewGuid(), snapshot, "{}", Guid.NewGuid());
        batch.StepExecutions.Add(new BatchStepExecution(batch.Id, snapshot.Steps[0].StepId, "S10", "heat", StepType.Heat, 0));
        return batch;
    }
}
