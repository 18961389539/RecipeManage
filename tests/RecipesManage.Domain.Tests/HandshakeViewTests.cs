using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Handshake;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class HandshakeViewTests
{
    [Theory]
    [InlineData(nameof(HandshakePhase.WaitingPlcReady), 0)]
    [InlineData(nameof(HandshakePhase.WritingParameters), 0)]
    [InlineData(nameof(HandshakePhase.AwaitingPlcAck), 1)]
    [InlineData(nameof(HandshakePhase.StepRunning), 2)]
    [InlineData("HostWait", 2)]
    [InlineData(nameof(HandshakePhase.Completing), 3)]
    [InlineData(nameof(HandshakePhase.ReadyToAdvance), 3)]
    public void FourStepIndex_ProjectsStateMachine(string phase, int step)
    {
        Assert.Equal(step, HandshakeView.FourStepIndex(phase));
    }

    [Theory]
    [InlineData("Held")]
    [InlineData("AwaitingConfirm")]
    [InlineData(nameof(HandshakePhase.Faulted))]
    [InlineData("Completed")]
    [InlineData("Released")]
    public void FourStepIndex_HostAndLifecycle_AreNotSteps(string phase)
    {
        Assert.Null(HandshakeView.FourStepIndex(phase));
    }

    [Fact]
    public void Token_ReadsMergedLane()
    {
        Assert.Equal("StepRunning", HandshakeView.Token("HT-01:StepRunning"));
        Assert.Equal("HT-01:StepRunning · HT-02:WaitingPlcReady",
            HandshakeView.Token("HT-01:StepRunning · HT-02:WaitingPlcReady"));
    }

    [Fact]
    public void Display_HeldBatch_UsesHeldNotLastLanePhase()
    {
        Assert.Equal(HandshakeView.Held, HandshakeView.Display("Held", nameof(HandshakePhase.ReadyToAdvance), "Held"));
        Assert.Equal(nameof(HandshakePhase.Faulted), HandshakeView.Display("Faulted", nameof(HandshakePhase.StepRunning), "Faulted"));
        Assert.Equal(nameof(HandshakePhase.ReadyToAdvance), HandshakeView.Display("Completed", nameof(HandshakePhase.ReadyToAdvance), "Completed"));
        Assert.Equal(nameof(HandshakePhase.StepRunning), HandshakeView.Display("Running", nameof(HandshakePhase.StepRunning), "Held"));
    }

    [Fact]
    public void SealCompleted_StopsAtReadyToAdvance_NotBatchStatus()
    {
        Assert.Equal(nameof(HandshakePhase.ReadyToAdvance), HandshakeView.SealCompleted("StepRunning"));
        Assert.Equal(nameof(HandshakePhase.Faulted), HandshakeView.SealCompleted(nameof(HandshakePhase.Faulted)));
        Assert.False(HandshakeView.IsLifecycleOverlay(HandshakeView.SealCompleted("Completed")));
    }

    /// <summary>工步结论不是握手相位：除保持/待确认两类叠加态，其余一律映射到相位令牌。</summary>
    [Theory]
    [InlineData(StepOutcome.Completed, "ReadyToAdvance")]
    [InlineData(StepOutcome.Skipped, "ReadyToAdvance")]
    [InlineData(StepOutcome.Running, "WaitingPlcReady")]
    [InlineData(StepOutcome.Pending, "WaitingPlcReady")]
    [InlineData(StepOutcome.Held, "Held")]
    [InlineData(StepOutcome.AwaitingConfirm, "AwaitingConfirm")]
    [InlineData(StepOutcome.Faulted, "Faulted")]
    public void FromStepOutcome_DoesNotCopyOutcomeIntoPhase(StepOutcome outcome, string phase)
    {
        Assert.Equal(phase, HandshakeView.FromStepOutcome(outcome));
    }
}
