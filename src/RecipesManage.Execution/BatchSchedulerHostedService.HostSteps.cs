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
/// 上位机自己完成的工步：质检归档、等待、人工确认。这三类不写 PLC，所以没有握手状态机。
/// </summary>
public sealed partial class BatchSchedulerHostedService
{
    private async Task<PhaseOutcome> RunHostSideStepAsync(
        LaneScope lane,
        SnapshotStep step,
        int index,
        bool resumeHeld,
        bool resumeCrashWait,
        WaveBarrier barrier,
        CancellationToken ct)
    {
        await IdlePlcAsync(lane.Plc, ct);
        await PublishIsa88Async(lane, step, index);
        await _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "step", new
        {
            stepId = step.StepId,
            stepIndex = index,
            outcome = "Running",
            unitProcedure = step.UnitProcedure,
            operation = step.Operation,
            equipmentCode = lane.Equipment.Code
        }), ct);

        if (PlcProgram.Kind(step.Type) == ExecutionKind.Wait)
            return await AwaitHostWaitAsync(lane, step, index, resumeHeld, resumeCrashWait, barrier, ct);

        IReadOnlyDictionary<string, double> measured = new Dictionary<string, double>();
        try
        {
            measured = await lane.Plc.ReadMeasuredAsync(ct);
        }
        catch
        {
            // 质检不依赖写 PLC；读不到实测时按设定值归档。
        }

        var bound = QualityArchive.Bind(step, measured);
        var quality = JsonSerializer.Serialize(bound, SnapshotJson.Options);
        var exec = lane.Batch.StepExecutions.Single(e => e.StepId == step.StepId);
        exec.MarkCompleted(DateTimeOffset.UtcNow, quality);
        await SetLanePhaseAsync(lane, "ReadyToAdvance", StepOutcome.Completed, step.StepId, step.Code, ct);
        lane.Db.HandshakeEvents.Add(new HandshakeEvent(
            lane.Batch.Id, step.StepId, step.Code, "ReadyToAdvance", "quality",
            $"[{lane.Equipment.Code}] 质检工步归档，禁止写 PLC", null));
        if (!await FlushAsync(lane, ct))
            return new PhaseOutcome(LaneResult.Terminated, null, null);

        await _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "step", new
        {
            stepId = step.StepId,
            stepIndex = index,
            outcome = "Completed",
            qualityJson = quality
        }), ct);

        var oos = await HoldIfQualityOosAsync(lane, step, exec, barrier, null, null, ct);
        return oos ?? new PhaseOutcome(LaneResult.Completed, null, null);
    }

    private async Task<PhaseOutcome> AwaitHostWaitAsync(
        LaneScope lane,
        SnapshotStep step,
        int index,
        bool resumeHeld,
        bool resumeCrashWait,
        WaveBarrier barrier,
        CancellationToken ct)
    {
        var planned = HostWaitClock.Planned(step);
        double? leftover = null;
        if (resumeHeld)
        {
            var holds = await lane.Db.HandshakeEvents
                .AsNoTracking()
                .Where(e => e.BatchId == lane.Batch.Id && e.StepId == step.StepId && e.Phase == "HostWait" && e.Kind == "hold")
                .ToListAsync(ct);
            leftover = holds
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => e.RemainingSeconds)
                .FirstOrDefault();
        }
        else if (resumeCrashWait)
        {
            var waits = await lane.Db.HandshakeEvents
                .AsNoTracking()
                .Where(e => e.BatchId == lane.Batch.Id && e.StepId == step.StepId && e.Kind == "wait")
                .ToListAsync(ct);
            var lastWait = waits.OrderByDescending(e => e.CreatedAt).FirstOrDefault();
            if (lastWait is not null)
                leftover = HostWaitClock.LeftoverAfterInterrupt(
                    planned, lastWait.CreatedAt, lastWait.RemainingSeconds, DateTimeOffset.UtcNow).TotalSeconds;
        }

        var resume = resumeHeld || resumeCrashWait;
        var duration = HostWaitClock.Resolve(planned, resume, leftover);
        var deadline = DateTimeOffset.UtcNow + duration;

        var detail = resumeHeld
            ? $"[{lane.Equipment.Code}] 恢复等待剩余 {duration.TotalSeconds:0.##}s，禁止写 PLC"
            : resumeCrashWait
                ? $"[{lane.Equipment.Code}] 引擎恢复等待剩余 {duration.TotalSeconds:0.##}s，禁止写 PLC"
                : $"[{lane.Equipment.Code}] 等待 {duration.TotalSeconds:0.##}s，禁止写 PLC";
        await SetLanePhaseAsync(lane, "HostWait", StepOutcome.Running, step.StepId, step.Code, ct);
        lane.Db.HandshakeEvents.Add(new HandshakeEvent(
            lane.Batch.Id, step.StepId, step.Code, "HostWait", "wait", detail, duration.TotalSeconds));
        if (!await FlushAsync(lane, ct))
            return new PhaseOutcome(LaneResult.Terminated, null, null);

        while (!ct.IsCancellationRequested)
        {
            if (barrier.Peek() is { } peerStop)
            {
                await IdlePlcAsync(lane.Plc, ct);
                if (peerStop == LaneResult.Held)
                {
                    var remainingPeer = Math.Max(0, (deadline - DateTimeOffset.UtcNow).TotalSeconds);
                    ExecOf(lane, step).MarkHeld();
                    await SetLanePhaseAsync(lane, "Held", StepOutcome.Held, step.StepId, step.Code, ct);
                    lane.Db.HandshakeEvents.Add(new HandshakeEvent(
                        lane.Batch.Id, step.StepId, step.Code, "HostWait", "hold",
                        $"[{lane.Equipment.Code}] 邻道保持，保存等待剩余", remainingPeer));
                    await FlushAsync(lane, ct);
                }

                return new PhaseOutcome(peerStop, null, null);
            }

            if (await ConsumeSkipAsync(lane, step.StepId, ct) is { } skipReason)
                return await SkipPhaseAsync(lane, step, index, new SkipGate(skipReason, "HostWait"), ct);

            if (TryGetHold(lane.Batch.Id, out var holdReason))
            {
                var remainingHold = Math.Max(0, (deadline - DateTimeOffset.UtcNow).TotalSeconds);
                return await HoldPhaseAsync(lane, step, holdReason, barrier, new HoldGate(
                    StepOutcome.Held, $"{holdReason}，剩余 {remainingHold:0.##}s",
                    Phase: "HostWait", RemainingSeconds: remainingHold), ct);
            }

            var remaining = Math.Max(0, (deadline - DateTimeOffset.UtcNow).TotalSeconds);
            await _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "handshake", new
            {
                phase = "HostWait",
                status = lane.Batch.Status.ToString(),
                stepId = step.StepId,
                stepIndex = index,
                stepName = step.Name,
                remainingSeconds = remaining,
                equipmentCode = lane.Equipment.Code,
                unitProcedure = step.UnitProcedure,
                stepCode = step.Code
            }), ct);

            if (remaining <= 0)
                break;

            await Task.Delay(120, ct);
        }

        ExecOf(lane, step).MarkCompleted(DateTimeOffset.UtcNow, "{}");
        await SetLanePhaseAsync(lane, "ReadyToAdvance", StepOutcome.Completed, step.StepId, step.Code, ct);
        lane.Db.HandshakeEvents.Add(new HandshakeEvent(
            lane.Batch.Id, step.StepId, step.Code, "ReadyToAdvance", "wait",
            $"[{lane.Equipment.Code}] 等待完成，未写 PLC", null));
        if (!await FlushAsync(lane, ct))
            return new PhaseOutcome(LaneResult.Terminated, null, null);

        await _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "step", new
        {
            stepId = step.StepId, stepIndex = index, outcome = "Completed"
        }), ct);
        return new PhaseOutcome(LaneResult.Completed, null, null);
    }

    private async Task<PhaseOutcome?> HoldIfQualityOosAsync(
        LaneScope lane,
        SnapshotStep step,
        BatchStepExecution exec,
        WaveBarrier barrier,
        HandshakeStateMachine? machine,
        HandshakeWorkContext? work,
        CancellationToken ct)
    {
        var oos = QualityArchive.EvaluateJson(step, exec.QualityJson).Where(r => r.OutOfSpec).ToList();
        if (oos.Count == 0)
            return null;

        var detail = string.Join("；", oos.Select(r => $"{r.Name}={r.Value:0.##} 规格[{r.Min},{r.Max}]"));
        await IdlePlcAsync(lane.Plc, ct);
        await SetLanePhaseAsync(lane, "Held", exec.Outcome, step.StepId, step.Code, ct);
        lane.Batch.Hold($"质检超差：{detail}");
        lane.Db.ProcessAlarms.Add(new ProcessAlarm(
            lane.Batch.Id, lane.Batch.BatchNo, step.StepId, step.Code,
            "QualityOos", "Quality", $"质检超差：{detail}", DateTimeOffset.UtcNow));
        if (machine is not null && work is not null)
            RecordHandshake(lane, step, machine, work, DateTimeOffset.UtcNow, "quality", $"超差 {detail}");
        else
            lane.Db.HandshakeEvents.Add(new HandshakeEvent(
                lane.Batch.Id, step.StepId, step.Code, "Held", "quality",
                $"[{lane.Equipment.Code}] 超差 {detail}", null));
        await FlushAsync(lane, ct);
        barrier.Signal(LaneResult.Held);
        await _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "held", new { reason = $"质检超差：{detail}", equipmentCode = lane.Equipment.Code }), ct);
        await _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "alarm", new
        {
            code = "QualityOos",
            message = $"质检超差：{detail}",
            stepCode = step.Code
        }), ct);
        return new PhaseOutcome(LaneResult.Held, machine, work);
    }

    private async Task<PhaseOutcome> AwaitOperatorConfirmAsync(
        LaneScope lane,
        SnapshotStep step,
        int index,
        WaveBarrier barrier,
        CancellationToken ct)
    {
        await IdlePlcAsync(lane.Plc, ct);
        ExecOf(lane, step).MarkAwaitingConfirm();
        await SetLanePhaseAsync(lane, "AwaitingConfirm", StepOutcome.AwaitingConfirm, step.StepId, step.Code, ct);
        lane.Db.HandshakeEvents.Add(new HandshakeEvent(
            lane.Batch.Id, step.StepId, step.Code, "AwaitingConfirm", "confirm",
            $"[{lane.Equipment.Code}] 人工确认工步，禁止写 PLC", null));
        if (!await FlushAsync(lane, ct))
            return new PhaseOutcome(LaneResult.Terminated, null, null);

        await _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "step", new
        {
            stepId = step.StepId,
            stepIndex = index,
            outcome = "AwaitingConfirm",
            unitProcedure = step.UnitProcedure,
            operation = step.Operation,
            equipmentCode = lane.Equipment.Code
        }), ct);
        await _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "handshake", new
        {
            phase = "AwaitingConfirm",
            status = lane.Batch.Status.ToString(),
            stepId = step.StepId,
            stepIndex = index,
            stepName = step.Name,
            remainingSeconds = (double?)null,
            equipmentCode = lane.Equipment.Code,
            unitProcedure = step.UnitProcedure,
            stepCode = step.Code
        }), ct);

        while (!ct.IsCancellationRequested)
        {
            if (barrier.Peek() is { } peerStop)
            {
                await IdlePlcAsync(lane.Plc, ct);
                return new PhaseOutcome(peerStop, null, null);
            }

            if (await ConsumeSkipAsync(lane, step.StepId, ct) is { } skipReason)
                return await SkipPhaseAsync(lane, step, index, new SkipGate(skipReason, "AwaitingConfirm"), ct);

            if (TryGetHold(lane.Batch.Id, out var holdReason))
                return await HoldPhaseAsync(lane, step, holdReason, barrier,
                    new HoldGate(StepOutcome.Held, holdReason, Phase: "AwaitingConfirm"), ct);

            if (await ConsumeConfirmAsync(lane, step.StepId, ct) is { } comment)
            {
                var quality = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["确认"] = "通过",
                    ["意见"] = string.IsNullOrWhiteSpace(comment) ? "操作员确认" : comment
                }, SnapshotJson.Options);
                ExecOf(lane, step).MarkCompleted(DateTimeOffset.UtcNow, quality);
                await SetLanePhaseAsync(lane, "ReadyToAdvance", StepOutcome.Completed, step.StepId, step.Code, ct);
                lane.Db.HandshakeEvents.Add(new HandshakeEvent(
                    lane.Batch.Id, step.StepId, step.Code, "ReadyToAdvance", "confirm",
                    $"[{lane.Equipment.Code}] 人工确认完成，未写 PLC", null));
                if (!await FlushAsync(lane, ct))
                    return new PhaseOutcome(LaneResult.Terminated, null, null);

                await _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "step", new
                {
                    stepId = step.StepId, stepIndex = index, outcome = "Completed", qualityJson = quality
                }), ct);
                return new PhaseOutcome(LaneResult.Completed, null, null);
            }

            await Task.Delay(120, ct);
        }

        return new PhaseOutcome(LaneResult.Faulted, null, null);
    }
}
