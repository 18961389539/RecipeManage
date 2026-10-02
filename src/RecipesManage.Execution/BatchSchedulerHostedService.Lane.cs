using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Execution;

/// <summary>
/// 单条车道的执行：车道级循环（按单元规程逐工步推进）与写 PLC 的四步握手循环（<see cref="RunPhaseAsync"/>）。
/// 编排（波次、屏障、批次完成）在 Waves.cs，上位机自己完成的工步在 HostSteps.cs。
/// </summary>
public sealed partial class BatchSchedulerHostedService
{
    private async Task<LaneResult> RunLaneAsync(
        Guid batchId,
        ControlRecipeSnapshot snapshot,
        EquipmentLine equipment,
        IPlcHandshakeClient plc,
        IReadOnlyList<string> units,
        bool multiLane,
        WaveBarrier barrier,
        CancellationToken hostCt)
    {
        var scope = _scopes.CreateAsyncScope();
        LaneScope? lane = null;
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var batch = await db.Batches.Include(b => b.StepExecutions).FirstOrDefaultAsync(b => b.Id == batchId, hostCt)
                        ?? throw new InvalidOperationException("批次不存在");
            var row = await db.Lanes.FirstOrDefaultAsync(
                l => l.BatchId == batchId && l.EquipmentId == equipment.Id, hostCt);

            lane = new LaneScope
            {
                Scope = scope,
                Db = db,
                Batch = batch,
                Equipment = equipment,
                Plc = plc,
                Snapshot = snapshot,
                MultiLane = multiLane,
                Row = row
            };

            var watchdog = HandshakeWatchdogOptions.FromJson(equipment.WatchdogJson);
            var firstOnPlc = true;

            foreach (var unit in units)
            {
                if (barrier.Peek() is { } stopLane)
                {
                    await IdlePlcAsync(plc, hostCt);
                    return stopLane;
                }

                var steps = snapshot.Steps
                    .Where(s => string.Equals(Isa88.UnitName(s.UnitProcedure), unit, StringComparison.Ordinal))
                    .ToList();
                SnapshotStep? last = null;
                HandshakeStateMachine? lastMachine = null;
                HandshakeWorkContext? lastWork = null;

                foreach (var step in steps)
                {
                    var result = await RunPhaseAsync(lane, step, watchdog, firstOnPlc, barrier, hostCt);
                    firstOnPlc = false;

                    if (result.Result == LaneResult.Terminated)
                        return LaneResult.Terminated;
                    if (result.Result != LaneResult.Completed)
                    {
                        barrier.Signal(result.Result);
                        return result.Result;
                    }

                    if (barrier.Peek() is { } after)
                    {
                        await IdlePlcAsync(plc, hostCt);
                        return after;
                    }

                    last = step;
                    lastMachine = result.Machine;
                    lastWork = result.Work;
                }

                if (last is not null && lastMachine is not null && lastWork is not null)
                {
                    RecordHandshake(lane, last, lastMachine, lastWork, DateTimeOffset.UtcNow, "isa88",
                        $"Unit Procedure 完成：{unit}");
                    if (!await FlushAsync(lane, hostCt))
                        return LaneResult.Terminated;

                    await _publisher.PublishAsync(new ExecutionEvent(batchId, "isa88-unit-complete", new
                    {
                        unitProcedure = unit,
                        stepCode = last.Code,
                        equipmentCode = equipment.Code
                    }), hostCt);
                }
            }

            return LaneResult.Completed;
        }
        finally
        {
            if (lane is not null)
                await lane.DisposeAsync();
            else
                await scope.DisposeAsync();
        }
    }

    private sealed record PhaseOutcome(LaneResult Result, HandshakeStateMachine? Machine, HandshakeWorkContext? Work);

    private async Task<PhaseOutcome> RunPhaseAsync(
        LaneScope lane,
        SnapshotStep step,
        HandshakeWatchdogOptions watchdog,
        bool resumeFromPlc,
        WaveBarrier barrier,
        CancellationToken ct)
    {
        var snapshot = lane.Snapshot;
        var batch = lane.Batch;
        var exec = batch.StepExecutions.Single(s => s.StepId == step.StepId);
        if (exec.Outcome is StepOutcome.Completed or StepOutcome.Skipped)
            return new PhaseOutcome(LaneResult.Completed, null, null);

        var index = snapshot.Steps.ToList().FindIndex(s => s.StepId == step.StepId);
        var resumeCrashWait = exec.Outcome == StepOutcome.Running;
        var resumeHeld = exec.Outcome == StepOutcome.Held;
        var phaseStartedAt = DateTimeOffset.UtcNow;
        void ApplyPhaseStart()
        {
            batch.AdvanceTo(step.StepId, Math.Max(index, 0));
            if (resumeHeld)
                exec.ResumeFromHold();
            else
                exec.MarkStarted(phaseStartedAt);
        }
        ApplyPhaseStart();

        if (!await FlushAsync(lane, ct, ApplyPhaseStart))
            return new PhaseOutcome(LaneResult.Terminated, null, null);

        if (PlcProgram.Kind(step.Type) == ExecutionKind.ManualConfirm)
            return await AwaitOperatorConfirmAsync(lane, step, index, barrier, ct);

        if (PlcProgram.Kind(step.Type) is ExecutionKind.QualityCheck or ExecutionKind.Wait)
            return await RunHostSideStepAsync(lane, step, index, resumeHeld, resumeCrashWait, barrier, ct);

        await PublishIsa88Async(lane, step, index);
        await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "step", new
        {
            stepId = step.StepId,
            stepIndex = index,
            outcome = "Running",
            unitProcedure = step.UnitProcedure,
            operation = step.Operation,
            equipmentCode = lane.Equipment.Code
        }), ct);

        var plc = lane.Plc;
        var work = ControlRecipeWritePlan.ToWorkContext(step);

        var inbound0 = await plc.ReadSignalsAsync(ct);
        if (resumeHeld || inbound0.PlcHeld)
        {
            await ReleasePlcHoldAsync(plc, watchdog.HoldAckTimeout, ct);
            lane.Db.HandshakeEvents.Add(new HandshakeEvent(
                batch.Id, step.StepId, step.Code, "StepRunning", "resume",
                $"[{lane.Equipment.Code}] Host_Hold=0", null));
            if (!await FlushAsync(lane, ct))
                return new PhaseOutcome(LaneResult.Terminated, null, null);
        }

        inbound0 = await plc.ReadSignalsAsync(ct);
        var resumeSession = resumeFromPlc || resumeHeld || inbound0.PlcHeld || inbound0.StepRunning || inbound0.StepComplete;
        var machine = resumeSession
            ? HandshakeStateMachine.ResumeFromPlc(inbound0, watchdog, DateTimeOffset.UtcNow)
            : new HandshakeStateMachine(watchdog, DateTimeOffset.UtcNow);

        if (machine.Phase is HandshakePhase.StepRunning or HandshakePhase.Completing or HandshakePhase.AwaitingPlcAck)
        {
            RecordHandshake(lane, step, machine, work, DateTimeOffset.UtcNow, "resume",
                "引擎恢复，从 PLC 当前握手相位继续，禁止重写参数");
            if (!await FlushAsync(lane, ct))
                return new PhaseOutcome(LaneResult.Terminated, null, null);
        }

        var lastSampleAt = DateTimeOffset.MinValue;

        while (machine.Phase is not HandshakePhase.ReadyToAdvance)
        {
            ct.ThrowIfCancellationRequested();
            if (barrier.Peek() is { } peerStop)
            {
                if (peerStop == LaneResult.Held && machine.Phase == HandshakePhase.StepRunning)
                    await CommandPlcHoldAsync(plc, watchdog.HoldAckTimeout, ct);
                else
                    await IdlePlcAsync(plc, ct);
                if (peerStop == LaneResult.Held && machine.Phase == HandshakePhase.StepRunning)
                {
                    exec.MarkHeld();
                    await SetLanePhaseAsync(lane, "Held", StepOutcome.Held, step.StepId, step.Code, ct);
                    await FlushAsync(lane, ct);
                }

                return new PhaseOutcome(peerStop, machine, work);
            }

            if (machine.Phase == HandshakePhase.WaitingPlcReady &&
                await ConsumeSkipAsync(lane, step.StepId, ct) is { } skipReason)
            {
                return await SkipPhaseAsync(lane, step, index,
                    new SkipGate(skipReason, Machine: machine, Work: work), ct);
            }

            if ((machine.Phase is HandshakePhase.WaitingPlcReady or HandshakePhase.StepRunning) &&
                TryGetHold(batch.Id, out var holdReason))
            {
                var runningHold = machine.Phase == HandshakePhase.StepRunning;
                return await HoldPhaseAsync(lane, step, holdReason, barrier, new HoldGate(
                    runningHold ? StepOutcome.Held : StepOutcome.Pending,
                    runningHold ? "Host_Hold=1 PLC_Held" : holdReason,
                    CommandPlcHold: runningHold,
                    Mark: runningHold ? HoldMark.Held : HoldMark.Pending,
                    HoldAckTimeout: watchdog.HoldAckTimeout,
                    Machine: machine,
                    Work: work), ct);
            }

            var now = DateTimeOffset.UtcNow;
            var inbound = await plc.ReadSignalsAsync(ct);
            await SetLanePhaseAsync(lane, machine.Phase.ToString(), exec.Outcome, step.StepId, step.Code, ct);

            if (now - lastSampleAt > TimeSpan.FromMilliseconds(400) &&
                machine.Phase is HandshakePhase.StepRunning or HandshakePhase.Completing)
            {
                var measured = await plc.ReadMeasuredAsync(ct);
                foreach (var (tag, value) in measured)
                    lane.Db.ProcessSamples.Add(new ProcessSample(batch.Id, step.StepId, now, tag, value, null));
                lastSampleAt = now;
                await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "sample", measured), ct);
            }

            await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "handshake", new
            {
                phase = machine.Phase.ToString(),
                status = batch.Status.ToString(),
                stepId = step.StepId,
                stepIndex = index,
                stepName = step.Name,
                remainingSeconds = machine.RemainingSeconds(work, now),
                inbound,
                equipmentCode = lane.Equipment.Code,
                unitProcedure = step.UnitProcedure,
                stepCode = step.Code
            }), ct);

            var phaseBefore = machine.Phase;
            var actions = machine.Tick(inbound, work, now);
            if (machine.Phase != phaseBefore)
                RecordHandshake(lane, step, machine, work, now, "phase", $"{phaseBefore}->{machine.Phase}");

            foreach (var action in actions)
                await ApplyAsync(action, lane, machine, exec, step, work, now, ct);

            if (exec.Outcome == StepOutcome.Completed)
            {
                await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "step", new
                {
                    stepId = step.StepId,
                    stepIndex = index,
                    outcome = "Completed",
                    qualityJson = exec.QualityJson
                }), ct);
            }

            if (machine.Phase == HandshakePhase.Faulted)
            {
                exec.MarkFaulted(machine.Fault?.Message ?? "握手故障");
                batch.Fault(machine.Fault?.Code.ToString() ?? "FAULT", machine.Fault?.Message ?? "握手故障");
                lane.Db.AuditLogs.Add(new AuditLog(null, "system", "batch.fault", "ProductionBatch", batch.Id.ToString(),
                    $"{machine.Fault?.Code}:{machine.Fault?.Message}"));
                RecordHandshake(lane, step, machine, work, now, "fault", machine.Fault?.Message);
                lane.Db.ProcessAlarms.Add(new ProcessAlarm(
                    batch.Id, batch.BatchNo, step.StepId, step.Code,
                    machine.Fault?.Code.ToString() ?? "FAULT", "Fault",
                    machine.Fault?.Message ?? "握手故障", now));
                await SetLanePhaseAsync(lane, "Faulted", StepOutcome.Faulted, step.StepId, step.Code, ct);
                await FlushAsync(lane, ct);
                await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "fault", machine.Fault!), ct);
                await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "alarm", new
                {
                    code = machine.Fault?.Code.ToString(),
                    message = machine.Fault?.Message,
                    stepCode = step.Code
                }), ct);
                return new PhaseOutcome(LaneResult.Faulted, machine, work);
            }

            await FlushAsync(lane, ct);
            await Task.Delay(100, ct);
        }

        if (exec.Outcome != StepOutcome.Skipped && TryGetHold(batch.Id, out var holdAfter))
        {
            return await HoldPhaseAsync(lane, step, holdAfter, barrier, new HoldGate(
                exec.Outcome, holdAfter, Mark: HoldMark.Keep, Machine: machine, Work: work), ct);
        }

        var oosHold = await HoldIfQualityOosAsync(lane, step, exec, barrier, machine, work, ct);
        return oosHold ?? new PhaseOutcome(LaneResult.Completed, machine, work);
    }
}
