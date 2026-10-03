using RecipesManage.Domain.Handshake;
using Xunit;

namespace RecipesManage.Domain.Tests;

/// <summary>
/// 读 PLC 中断后的状态机行为。中断期间上位机没有任何观测，所以：
/// 心跳基准要顺延（否则恢复后第一次读就会误判 HeartbeatLost）；真实流逝的时限（执行时限）不顺延；
/// 窗口用尽则只能停下报 PlcCommLost。
/// </summary>
public sealed class ReadGapTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-03T08:00:00+08:00");
    private static readonly HandshakeWatchdogOptions Options = new() { HeartbeatTimeout = TimeSpan.FromSeconds(3) };

    private static PlcInboundSignals Running(uint heartbeat) =>
        new(PlcReady: false, StepRunning: true, StepComplete: false, StepError: false, ErrorCode: 0,
            Heartbeat: heartbeat, TriggerWriteEcho: true);

    private static HandshakeWorkContext Work(double executionSeconds) =>
        new(1, 1, new float[16], TimeSpan.FromSeconds(executionSeconds));

    private static HandshakeStateMachine RunningMachine() =>
        HandshakeStateMachine.ResumeFromPlc(Running(5), Options, T0);

    [Fact]
    public void WithoutTheGapNote_AFrozenHeartbeatAcrossAnOutage_IsMisreadAsHeartbeatLost()
    {
        // 基线：说明为什么需要 NoteReadGap——断前心跳 5，断了 10 秒，恢复后读到的仍是 5（比如 PLC 这一刻还没翻转）。
        var machine = RunningMachine();
        machine.Tick(Running(5), Work(600), T0.AddSeconds(10));
        Assert.Equal(HandshakeFaultCode.HeartbeatLost, machine.Fault?.Code);
    }

    [Fact]
    public void AfterAnOutage_TheHeartbeatBaselineIsShifted_SoTheFirstReadIsNotMisjudged()
    {
        var machine = RunningMachine();
        machine.NoteReadGap(TimeSpan.FromSeconds(10));

        machine.Tick(Running(5), Work(600), T0.AddSeconds(10));
        Assert.Equal(HandshakePhase.StepRunning, machine.Phase);
        Assert.Null(machine.Fault);
    }

    [Fact]
    public void ShiftingTheBaseline_DoesNotHideAHeartbeatThatIsReallyStuck()
    {
        // 顺延只抵消"没观测的那段"，恢复之后心跳仍不动照样要判丢失。
        var machine = RunningMachine();
        machine.NoteReadGap(TimeSpan.FromSeconds(10));
        machine.Tick(Running(5), Work(600), T0.AddSeconds(10));

        machine.Tick(Running(5), Work(600), T0.AddSeconds(14));
        Assert.Equal(HandshakeFaultCode.HeartbeatLost, machine.Fault?.Code);
    }

    [Fact]
    public void TheExecutionDeadline_IsWallClock_AndIsNotExtendedByAnOutage()
    {
        // 工步执行时限是真实流逝的时间；炉子在断线期间照常在烧。
        var machine = RunningMachine();
        machine.NoteReadGap(TimeSpan.FromSeconds(10));

        machine.Tick(Running(6), Work(5), T0.AddSeconds(10));
        Assert.Equal(HandshakeFaultCode.ExecutionTimeout, machine.Fault?.Code);
    }

    [Fact]
    public void ANonPositiveGap_ChangesNothing()
    {
        var machine = RunningMachine();
        machine.NoteReadGap(TimeSpan.Zero);
        machine.NoteReadGap(TimeSpan.FromSeconds(-5));
        machine.Tick(Running(5), Work(600), T0.AddSeconds(10));
        Assert.Equal(HandshakeFaultCode.HeartbeatLost, machine.Fault?.Code);
    }

    [Fact]
    public void CommLost_FaultsTheMachine_WithTheMessage()
    {
        var machine = RunningMachine();
        machine.NotifyCommLost(T0.AddSeconds(31), "读 PLC 连续失败");

        Assert.Equal(HandshakePhase.Faulted, machine.Phase);
        Assert.Equal(HandshakeFaultCode.PlcCommLost, machine.Fault?.Code);
        Assert.Equal("读 PLC 连续失败", machine.Fault?.Message);
        // 已故障的机器不再接任何输入。
        Assert.Empty(machine.Tick(Running(9), Work(600), T0.AddSeconds(32)));
    }

    [Fact]
    public void CommLost_DoesNotOverwriteAnEarlierFault()
    {
        var machine = RunningMachine();
        machine.NotifyWriteVerifyFailed(T0, "回读不一致");
        machine.NotifyCommLost(T0.AddSeconds(31), "读 PLC 连续失败");
        Assert.Equal(HandshakeFaultCode.WriteVerifyMismatch, machine.Fault?.Code);
    }

    [Fact]
    public void WatchdogOptions_ReadTheToleranceSettings_AndDefaultToThirtySecondsAndOneSecond()
    {
        var defaults = HandshakeWatchdogOptions.FromJson(null);
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.ReadFailureTolerance);
        Assert.Equal(TimeSpan.FromSeconds(1), defaults.ReadRetryInterval);

        var set = HandshakeWatchdogOptions.FromJson("""{"readFailureToleranceSeconds":60,"readRetrySeconds":0.25}""");
        Assert.Equal(TimeSpan.FromSeconds(60), set.ReadFailureTolerance);
        Assert.Equal(TimeSpan.FromSeconds(0.25), set.ReadRetryInterval);

        // 非正数一律回落默认值（与其它看门狗参数一致），不存在"配 0 关闭容忍"这种隐含语义。
        var bad = HandshakeWatchdogOptions.FromJson("""{"readFailureToleranceSeconds":0,"readRetrySeconds":-1}""");
        Assert.Equal(TimeSpan.FromSeconds(30), bad.ReadFailureTolerance);
        Assert.Equal(TimeSpan.FromSeconds(1), bad.ReadRetryInterval);
    }
}
