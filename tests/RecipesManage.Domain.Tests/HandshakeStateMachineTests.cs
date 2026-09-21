using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public class HandshakeStateMachineTests
{
    private static readonly HandshakeWorkContext Work = new(
        StepId: 10,
        StepType: 1,
        Parameters: [530f, 8f],
        ExecutionTimeout: TimeSpan.FromSeconds(30));

    private static PlcInboundSignals Idle() => new(true, false, false, false, 0, 1, false);
    private static PlcInboundSignals BusyNotReady() => new(false, false, false, false, 0, 1, false);

    [Fact]
    public void Does_not_write_until_plc_ready()
    {
        var now = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
        var machine = new HandshakeStateMachine(now: now);
        var actions = machine.Tick(BusyNotReady(), Work, now);
        Assert.Empty(actions);
        Assert.Equal(HandshakePhase.WaitingPlcReady, machine.Phase);
    }

    [Fact]
    public void Happy_path_four_step_handshake()
    {
        var now = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
        var machine = new HandshakeStateMachine(now: now);

        var a = machine.Tick(Idle(), Work, now);
        var write = Assert.IsType<WriteStepPayloadAction>(Assert.Single(a));
        Assert.Equal(10, write.StepId);
        Assert.Equal(HandshakePhase.WritingParameters, machine.Phase);

        machine.NotifyParametersWritten(now);
        now = now.AddMilliseconds(100);
        var trigger = Assert.IsType<AssertTriggerWriteAction>(Assert.Single(machine.Tick(Idle(), Work, now)));
        Assert.True(trigger.Value);
        Assert.Equal(HandshakePhase.AwaitingPlcAck, machine.Phase);
        machine.NotifyTriggerAsserted();

        now = now.AddMilliseconds(100);
        var running = new PlcInboundSignals(false, true, false, false, 0, 2, true);
        Assert.Empty(machine.Tick(running, Work, now));
        Assert.Equal(HandshakePhase.StepRunning, machine.Phase);

        now = now.AddSeconds(1);
        running = running with { Heartbeat = 3 };
        Assert.Empty(machine.Tick(running, Work, now));

        now = now.AddSeconds(1);
        var complete = new PlcInboundSignals(false, false, true, false, 0, 4, true);
        var archive = Assert.IsType<ArchiveMeasurementsAction>(Assert.Single(machine.Tick(complete, Work, now)));
        Assert.Equal(HandshakePhase.Completing, machine.Phase);
        machine.NotifyArchiveCompleted();

        now = now.AddMilliseconds(50);
        var resets = machine.Tick(complete, Work, now);
        Assert.Contains(resets, x => x is ResetCompleteAction);
        Assert.Contains(resets, x => x is AssertTriggerWriteAction { Value: false });
        machine.NotifyResetIssued();

        now = now.AddMilliseconds(50);
        var advance = Assert.IsType<AdvanceStepAction>(Assert.Single(machine.Tick(Idle(), Work, now)));
        Assert.Equal(HandshakePhase.ReadyToAdvance, machine.Phase);
        _ = archive;
        _ = advance;
    }

    [Fact]
    public void Plc_error_faults_and_stops_writing()
    {
        var now = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
        var machine = new HandshakeStateMachine(now: now);
        machine.Tick(Idle(), Work, now);
        machine.NotifyParametersWritten(now);
        machine.Tick(Idle(), Work, now.AddMilliseconds(20));
        machine.NotifyTriggerAsserted();

        var faulted = new PlcInboundSignals(false, false, false, true, 51, 2, true);
        var action = Assert.IsType<RaiseFaultAction>(Assert.Single(machine.Tick(faulted, Work, now.AddSeconds(1))));
        Assert.Equal(HandshakeFaultCode.PlcReportedError, action.Fault.Code);
        Assert.Equal(51, action.Fault.PlcErrorCode);
        Assert.Equal(HandshakePhase.Faulted, machine.Phase);
        Assert.Empty(machine.Tick(Idle(), Work, now.AddSeconds(2)));
    }

    [Fact]
    public void Heartbeat_loss_during_running_faults()
    {
        var now = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
        var watchdog = new HandshakeWatchdogOptions { HeartbeatTimeout = TimeSpan.FromSeconds(3) };
        var machine = new HandshakeStateMachine(watchdog, now);
        machine.Tick(Idle(), Work, now);
        machine.NotifyParametersWritten(now);
        machine.Tick(Idle(), Work, now);
        machine.NotifyTriggerAsserted();
        var running = new PlcInboundSignals(false, true, false, false, 0, 9, true);
        machine.Tick(running, Work, now.AddMilliseconds(50));

        var later = now.AddSeconds(4);
        var action = Assert.IsType<RaiseFaultAction>(Assert.Single(machine.Tick(running, Work, later)));
        Assert.Equal(HandshakeFaultCode.HeartbeatLost, action.Fault.Code);
    }

    [Fact]
    public void NotifyParametersWritten_OutsideWritingPhase_BlindWriteRejected()
    {
        var now = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
        var machine = new HandshakeStateMachine(now: now);
        machine.NotifyParametersWritten(now);
        Assert.Equal(HandshakePhase.Faulted, machine.Phase);
        Assert.Equal(HandshakeFaultCode.BlindWriteRejected, machine.Fault!.Code);
        Assert.Empty(machine.Tick(Idle(), Work, now.AddMilliseconds(20)));
    }

    [Fact]
    public void TriggerAlreadyHighDuringWrite_BlindWriteRejected()
    {
        var now = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
        var machine = new HandshakeStateMachine(now: now);
        machine.Tick(Idle(), Work, now);
        Assert.Equal(HandshakePhase.WritingParameters, machine.Phase);
        var leftover = Idle() with { TriggerWriteEcho = true };
        var action = Assert.IsType<RaiseFaultAction>(Assert.Single(machine.Tick(leftover, Work, now.AddMilliseconds(20))));
        Assert.Equal(HandshakeFaultCode.BlindWriteRejected, action.Fault.Code);
    }

    [Fact]
    public void Resume_from_running_does_not_rewrite_payload()
    {
        var now = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
        var inbound = new PlcInboundSignals(false, true, false, false, 0, 12, true);
        var machine = HandshakeStateMachine.ResumeFromPlc(inbound, null, now);
        Assert.Equal(HandshakePhase.StepRunning, machine.Phase);
        Assert.Empty(machine.Tick(inbound, Work, now));
    }

    [Fact]
    public void RemainingSeconds_CountsDownWhileWaitingForReady()
    {
        var now = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
        var machine = new HandshakeStateMachine(now: now);
        var first = machine.RemainingSeconds(Work, now);
        var later = machine.RemainingSeconds(Work, now.AddSeconds(4));
        Assert.True(first > later);
        Assert.InRange(later ?? -1, 10, 12);
    }

    [Fact]
    public void RemainingSeconds_StepRunning_UsesProcessDurationNotWatchdog()
    {
        var now = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
        var machine = new HandshakeStateMachine(now: now);
        var work = Work with { ProcessDuration = TimeSpan.FromSeconds(8) };
        machine.Tick(Idle(), work, now);
        machine.NotifyParametersWritten(now);
        now = now.AddMilliseconds(50);
        machine.Tick(Idle(), work, now);
        machine.NotifyTriggerAsserted();
        now = now.AddMilliseconds(50);
        var running = new PlcInboundSignals(false, true, false, false, 0, 2, true);
        machine.Tick(running, work, now);
        Assert.Equal(HandshakePhase.StepRunning, machine.Phase);
        var remaining = machine.RemainingSeconds(work, now.AddSeconds(2));
        Assert.InRange(remaining ?? -1, 5.5, 6.5);
        Assert.True(remaining < 25);
    }
}

public class RecipeTopologyTests
{
    [Fact]
    public void Detects_cycle()
    {
        var v = Guid.NewGuid();
        var a = NewStep(v, "A", 0);
        var b = NewStep(v, "B", 1);
        var ex = Assert.Throws<RecipesManage.Domain.Common.DomainException>(() =>
            RecipeTopology.Order([a, b], [new RecipeEdge(v, a.Id, b.Id), new RecipeEdge(v, b.Id, a.Id)]));
        Assert.Equal("CYCLE", ex.Code);
    }

    [Fact]
    public void Rejects_cross_unit_edge_from_mid_unit()
    {
        var v = Guid.NewGuid();
        var a = NewStep(v, "S10", 0, "UP-A");
        var b = NewStep(v, "S20", 1, "UP-A");
        var c = NewStep(v, "S30", 2, "UP-B");
        var ex = Assert.Throws<RecipesManage.Domain.Common.DomainException>(() =>
            RecipeTopology.Validate([a, b, c],
            [
                new RecipeEdge(v, a.Id, b.Id),
                new RecipeEdge(v, a.Id, c.Id)
            ]));
        Assert.Equal("ISA88_UNIT", ex.Code);
    }

    [Fact]
    public void Allows_unit_boundary_then_reports_serial_waves()
    {
        var v = Guid.NewGuid();
        var a = NewStep(v, "S10", 0, "UP-A");
        var b = NewStep(v, "S20", 1, "UP-A");
        var c = NewStep(v, "S30", 2, "UP-B");
        RecipeTopology.Validate([a, b, c],
        [
            new RecipeEdge(v, a.Id, b.Id),
            new RecipeEdge(v, b.Id, c.Id)
        ]);
        var waves = RecipeTopology.UnitProcedureWaves([a, b, c],
        [
            new RecipeEdge(v, a.Id, b.Id),
            new RecipeEdge(v, b.Id, c.Id)
        ]);
        Assert.Equal(2, waves.Count);
        Assert.Equal(["UP-A"], waves[0]);
        Assert.Equal(["UP-B"], waves[1]);
    }

    [Fact]
    public void Parallel_units_with_no_cross_edge_form_one_wave()
    {
        var v = Guid.NewGuid();
        var a = NewStep(v, "S10", 0, "UP-A");
        var b = NewStep(v, "S20", 1, "UP-B");
        var waves = RecipeTopology.UnitProcedureWaves([a, b], []);
        Assert.Single(waves);
        Assert.Equal(["UP-A", "UP-B"], waves[0]);
    }

    private static RecipeStep NewStep(Guid versionId, string code, int ordinal, string? unit = null) =>
        new(versionId, code, code, StepType.Hold, ordinal, 0, 0, 30, null, [], unitProcedure: unit);
}
