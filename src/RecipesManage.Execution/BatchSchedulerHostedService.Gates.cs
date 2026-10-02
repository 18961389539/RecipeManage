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
/// 保持 / 跳步这两道门：引擎在握手环、上位机等待环、人工确认环里遇到操作员请求时的落地方式，
/// 以及保持意图的取走与意图行的同事务删除。
/// </summary>
public sealed partial class BatchSchedulerHostedService
{
    /// <summary>保持时工步自己标成什么：运行中的相留痕为 Held，还没动 PLC 的回退成 Pending，已经完成的不动。</summary>
    private enum HoldMark { Held, Pending, Keep }

    /// <summary>
    /// 一次"保持"的落地参数。引擎里 PLC 握手环、上位机等待环、人工确认环、以及握手环收尾处的
    /// 四处保持分支，除了这些参数以外做的是同一件事（见 <see cref="HoldPhaseAsync"/>）。
    /// </summary>
    private sealed record HoldGate(
        StepOutcome Outcome,
        string Detail,
        bool CommandPlcHold = false,
        HoldMark Mark = HoldMark.Held,
        TimeSpan? HoldAckTimeout = null,
        string? Phase = null,
        double? RemainingSeconds = null,
        bool HostHold = false,
        HandshakeStateMachine? Machine = null,
        HandshakeWorkContext? Work = null);

    /// <summary>一次"跳步"的落地参数：同上，三处分支只差履历里那个相位与正文。</summary>
    private sealed record SkipGate(
        string Reason,
        string? Phase = null,
        HandshakeStateMachine? Machine = null,
        HandshakeWorkContext? Work = null);

    /// <summary>
    /// 保持的六段式：停 PLC → 标工步 → 车道相位 → 批次保持并消费意图 → 落履历 → 落库 → 通知同批与界面。
    ///
    /// 顺序里唯一有语义的是"批次保持"必须在落库之前：FlushAsync 会把整批状态一起写，
    /// 顺序反了就会出现"车道已 Held 但批次还是 Running"的可见中间态。
    /// </summary>
    private async Task<PhaseOutcome> HoldPhaseAsync(
        LaneScope lane,
        SnapshotStep step,
        string reason,
        WaveBarrier barrier,
        HoldGate gate,
        CancellationToken ct)
    {
        var batch = lane.Batch;
        if (gate.CommandPlcHold)
            await CommandPlcHoldAsync(lane.Plc, gate.HoldAckTimeout ?? TimeSpan.FromSeconds(5), ct);
        else
            await IdlePlcAsync(lane.Plc, ct);

        var exec = ExecOf(lane, step);
        switch (gate.Mark)
        {
            case HoldMark.Held:
                exec.MarkHeld();
                break;
            case HoldMark.Pending:
                exec.RevertToPending();
                break;
        }

        await SetLanePhaseAsync(lane, "Held", gate.Outcome, step.StepId, step.Code, ct);
        batch.Hold(reason);
        await ConsumeHoldAsync(lane, ct);
        RecordGate(lane, step, "hold", gate.Detail, gate.Phase, gate.RemainingSeconds, gate.Machine, gate.Work);
        await FlushAsync(lane, ct);
        barrier.Signal(LaneResult.Held);
        await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "held", new
        {
            reason,
            equipmentCode = lane.Equipment.Code,
            hostHold = gate.HostHold
        }), ct);
        return new PhaseOutcome(LaneResult.Held, gate.Machine, gate.Work);
    }

    /// <summary>
    /// 跳步的六段式：停 PLC → 标 Skipped → 车道放行 → 落履历 → 落库 → 广播步进。
    /// 落库失败（批次被并发改成终态）时返回 Terminated，与保持不同：不通知同批，也不放行。
    /// </summary>
    private async Task<PhaseOutcome> SkipPhaseAsync(
        LaneScope lane,
        SnapshotStep step,
        int index,
        SkipGate gate,
        CancellationToken ct)
    {
        await IdlePlcAsync(lane.Plc, ct);
        ExecOf(lane, step).MarkSkipped(gate.Reason);
        await SetLanePhaseAsync(lane, "ReadyToAdvance", StepOutcome.Skipped, step.StepId, step.Code, ct);
        RecordGate(lane, step, "skip", gate.Reason, gate.Phase, null, gate.Machine, gate.Work);
        if (!await FlushAsync(lane, ct))
            return new PhaseOutcome(LaneResult.Terminated, gate.Machine, gate.Work);

        await _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "step", new
        {
            stepId = step.StepId,
            stepIndex = index,
            outcome = "Skipped"
        }), ct);
        return new PhaseOutcome(LaneResult.Completed, gate.Machine, gate.Work);
    }

    /// <summary>
    /// 握手履历一行：有状态机时相位与剩余秒数取自它，上位机三类没有状态机，只能由调用方给相位。
    /// </summary>
    private void RecordGate(
        LaneScope lane,
        SnapshotStep step,
        string kind,
        string detail,
        string? phase,
        double? remainingSeconds,
        HandshakeStateMachine? machine,
        HandshakeWorkContext? work)
    {
        if (machine is not null && work is not null)
        {
            RecordHandshake(lane, step, machine, work, DateTimeOffset.UtcNow, kind, detail);
            return;
        }

        lane.Db.HandshakeEvents.Add(new HandshakeEvent(
            lane.Batch.Id, step.StepId, step.Code, phase ?? "Held", kind,
            $"[{lane.Equipment.Code}] {detail}", remainingSeconds));
    }

    private bool TryGetHold(Guid batchId, out string reason) =>
        _holdReasons.TryGetValue(batchId, out reason!);

    /// <summary>保持意图的删除与批次转 Held 同一次落库（调用方紧接着 <see cref="FlushAsync"/>）。</summary>
    private async Task ConsumeHoldAsync(LaneScope lane, CancellationToken ct)
    {
        _holdReasons.TryRemove(lane.Batch.Id, out _);
        await ForgetIntentAsync(lane, SchedulerIntentKinds.Hold, null, ct);
    }
}
