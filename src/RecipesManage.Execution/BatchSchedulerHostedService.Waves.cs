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
using RecipesManage.Infrastructure.Persistence;

namespace RecipesManage.Execution;

/// <summary>
/// ISA-88 单元波次执行。
///
/// 这里有三个刻意的结构约束：
/// 1. <b>每条 lane 独占一个 IServiceScope / DbContext</b>。DbContext 非线程安全，
///    历史上整个批次共用一个 DbContext + 一把手写 SemaphoreSlim(gate) 凑合，
///    而 gate 的保护范围被随意突破（部分 db 读在锁外）。现在 lane 之间不共享任何可变对象。
/// 2. <b>车道相位写自己的 batch_lanes 行</b>，不写 production_batches。
///    否则多 lane 每 tick 争抢同一行批次记录，还与 HTTP 控制写互相覆盖。
/// 3. <b>批次级状态变更走短事务 + 乐观并发</b>。生产批次带 ConcurrencyStamp，
///    操作员的中止/保持若在调度 tick 之后落盘，调度侧 SaveChanges 会命中 0 行，
///    重新加载后发现批次已进终态并收敛退出 —— 而不是把操作员的指令覆盖掉。
/// </summary>
public sealed partial class BatchSchedulerHostedService
{
    private enum LaneResult { Completed, Held, Faulted, Terminated }

    private sealed class WaveBarrier
    {
        private int _state;

        public void Signal(LaneResult result)
        {
            if (result is not (LaneResult.Held or LaneResult.Faulted))
                return;
            Interlocked.CompareExchange(ref _state, result == LaneResult.Held ? 1 : 2, 0);
        }

        public LaneResult? Peek() => Volatile.Read(ref _state) switch
        {
            1 => LaneResult.Held,
            2 => LaneResult.Faulted,
            _ => null
        };
    }

    /// <summary>一条 lane 独占的运行上下文。lane 之间不共享 DbContext，也不共享实体。</summary>
    private sealed class LaneScope : IAsyncDisposable
    {
        public required AsyncServiceScope Scope { get; init; }
        public required AppDbContext Db { get; init; }
        public required ProductionBatch Batch { get; init; }
        public required EquipmentLine Equipment { get; init; }
        public required IPlcHandshakeClient Plc { get; init; }
        public required ControlRecipeSnapshot Snapshot { get; init; }
        public required bool MultiLane { get; init; }
        public BatchLane? Row { get; set; }

        public async ValueTask DisposeAsync() => await Scope.DisposeAsync();
    }

    // 终态名单唯一来源在域层，这里只是给按 status 取值的调用点留个简写。
    private static bool IsTerminal(BatchStatus status) => ProductionBatch.IsTerminalState(status);

    private async Task RunUnitWavesAsync(Guid batchId, CancellationToken ct)
    {
        using var bootScope = _scopes.CreateScope();
        var boot = bootScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var batch = await boot.Batches.Include(b => b.StepExecutions).FirstOrDefaultAsync(b => b.Id == batchId, ct)
                    ?? throw new InvalidOperationException("批次不存在");
        if (batch.Status is BatchStatus.Held or BatchStatus.Aborted or BatchStatus.Completed
             or BatchStatus.Released or BatchStatus.DispositionRejected)
            return;

        var snapshot = BatchService.Deserialize(batch.ControlRecipeJson)
                       ?? throw new InvalidOperationException("快照损坏");
        var boundIds = UnitEquipmentBinding.AllIds(snapshot, batch.EquipmentId);
        var equipmentRows = await boot.Equipment.Where(e => boundIds.Contains(e.Id)).ToListAsync(ct);
        if (equipmentRows.Count != boundIds.Count)
            throw new InvalidOperationException("批次绑定的设备缺失。");

        await EnsureLaneRowsAsync(batchId, snapshot, equipmentRows, ct);

        batch.MarkRunning(DateTimeOffset.UtcNow);
        await boot.SaveChangesAsync(ct);

        var waves = RecipeTopology.UnitProcedureWaves(
            snapshot.Steps.Select(s => (s.StepId, Isa88.UnitName(s.UnitProcedure))).ToList(),
            snapshot.Edges.Select(e => (e.FromStepId, e.ToStepId)).ToList());
        await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "isa88", new
        {
            waves,
            equipment = equipmentRows.Select(e => e.Code).ToList()
        }), ct);

        var plcs = new Dictionary<Guid, IPlcHandshakeClient>();
        try
        {
            foreach (var eq in equipmentRows)
            {
                var client = _drivers.Create(eq);
                await client.ConnectAsync(ct);
                plcs[eq.Id] = client;
            }

            foreach (var wave in waves)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsBatchTerminalAsync(batchId, ct))
                    return;

                var barrier = new WaveBarrier();
                var groups = wave
                    .GroupBy(unit => UnitEquipmentBinding.Resolve(snapshot, unit, batch.EquipmentId))
                    .ToList();

                var results = await Task.WhenAll(groups.Select(group => RunLaneAsync(
                    batchId,
                    snapshot,
                    equipmentRows.Single(e => e.Id == group.Key),
                    plcs[group.Key],
                    group.ToList(),
                    equipmentRows.Count > 1,
                    barrier,
                    ct)));

                if (results.Any(r => r != LaneResult.Completed) || barrier.Peek() is not null)
                    return;
            }

            await MarkBatchCompletedAsync(batchId, ct);
        }
        finally
        {
            foreach (var plc in plcs.Values)
                await plc.DisposeAsync();
        }
    }

    private async Task<bool> IsBatchTerminalAsync(Guid batchId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var status = await db.Batches.AsNoTracking()
            .Where(b => b.Id == batchId)
            .Select(b => b.Status)
            .FirstOrDefaultAsync(ct);
        return IsTerminal(status);
    }

    private async Task MarkBatchCompletedAsync(Guid batchId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var batch = await db.Batches.FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null || IsTerminal(batch.Status))
            return;

        batch.Complete(DateTimeOffset.UtcNow);
        await RemoveIntentsAsync(db, batchId, ct);
        await db.SaveChangesAsync(ct);
        await ReleaseEquipmentAsync(batchId, ct);
        await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "completed", new { batch.BatchNo }), ct);
        await OccupancyRealtime.PublishAsync(db, _publisher, batch.Id, ct);
    }

    /// <summary>批次离开设备（完成/中止/放行/拒收）时释放设备租约。</summary>
    private async Task ReleaseEquipmentAsync(Guid batchId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<EquipmentLeaseService>().ReleaseAsync(batchId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "批次 {BatchId} 释放设备租约失败", batchId);
        }
    }

    private async Task EnsureLaneRowsAsync(
        Guid batchId,
        ControlRecipeSnapshot snapshot,
        IReadOnlyList<EquipmentLine> equipmentRows,
        CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.Lanes.Where(l => l.BatchId == batchId).Select(l => l.EquipmentId).ToListAsync(ct);

        foreach (var equipment in equipmentRows)
        {
            if (existing.Contains(equipment.Id))
                continue;
            db.Lanes.Add(new BatchLane(batchId, equipment.Id, equipment.Code, string.Empty));
        }

        await db.SaveChangesAsync(ct);
    }

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
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
        ConsumeHold(batch.Id);
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
                TryConsumeSkip(batch.Id, step.StepId, out var skipReason))
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
        var quality = JsonSerializer.Serialize(bound, BatchService.JsonOptions);
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

            if (TryConsumeSkip(lane.Batch.Id, step.StepId, out var skipReason))
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

            if (TryConsumeSkip(lane.Batch.Id, step.StepId, out var skipReason))
                return await SkipPhaseAsync(lane, step, index, new SkipGate(skipReason, "AwaitingConfirm"), ct);

            if (TryGetHold(lane.Batch.Id, out var holdReason))
                return await HoldPhaseAsync(lane, step, holdReason, barrier,
                    new HoldGate(StepOutcome.Held, holdReason, Phase: "AwaitingConfirm"), ct);

            if (TryConsumeConfirm(lane.Batch.Id, step.StepId, out var comment))
            {
                var quality = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["确认"] = "通过",
                    ["意见"] = string.IsNullOrWhiteSpace(comment) ? "操作员确认" : comment
                }, BatchService.JsonOptions);
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

    private static BatchStepExecution ExecOf(LaneScope lane, SnapshotStep step) =>
        lane.Batch.StepExecutions.Single(e => e.StepId == step.StepId);

    private Task PublishIsa88Async(LaneScope lane, SnapshotStep step, int index) =>
        _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "isa88", new
        {
            unitProcedure = step.UnitProcedure,
            operation = step.Operation,
            stepCode = step.Code,
            stepIndex = index,
            equipmentCode = lane.Equipment.Code
        }));

    /// <summary>
    /// 车道相位只在变化时落盘：写的是 lane 私有的 batch_lanes 行，不与其他 lane 竞争批次行。
    /// production_batches.HandshakePhase 只是给 UI/历史数据的汇总视图，这里只改内存，
    /// 由紧随其后的 <see cref="FlushAsync"/> 统一带乐观并发地持久化。
    /// </summary>
    private async Task SetLanePhaseAsync(
        LaneScope lane,
        string phase,
        StepOutcome outcome,
        Guid? stepId,
        string? stepCode,
        CancellationToken ct)
    {
        var row = lane.Row;
        if (row is null)
            return;
        if (row.Phase == phase && row.Outcome == outcome && row.StepId == stepId)
            return;

        row.Update(phase, outcome, stepId, stepCode);
        try
        {
            await lane.Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 批次行被并发改写时本行仍未落盘：留给后续 FlushAsync 重载后统一收敛。
        }

        lane.Batch.UpdateHandshake(await MergedPhaseAsync(lane, ct));
    }

    /// <summary>批次级展示串由 <see cref="BatchLanes.Format"/> 从车道行汇总——格式的唯一定义在域层。</summary>
    private static async Task<string> MergedPhaseAsync(LaneScope lane, CancellationToken ct)
    {
        var rows = await lane.Db.Lanes.AsNoTracking()
            .Where(l => l.BatchId == lane.Batch.Id)
            .ToListAsync(ct);

        return BatchLanes.Format(rows, lane.Batch.HandshakePhase);
    }

    /// <summary>
    /// 批次级写入。<see cref="ProductionBatch.ConcurrencyStamp"/> 让并发冲突显式失败；
    /// 冲突后重新加载实体，若发现批次已被 HTTP 路径推进到终态则返回 false，
    /// 由调用方终止本 lane —— 保证操作员的中止/放行不会被调度 tick 覆盖。
    /// 重载会把本轮状态机写入一并回滚：<paramref name="replayEngineWrites"/> 传入引擎独有
    /// 写（步骤推进 / Running 标记，HTTP 路径不碰这些字段）的重放闭包，冲突后先重放再重试，
    /// 避免内存状态机已推进而库里仍是旧步骤状态——崩溃恢复会据此重复执行同一工步。
    /// 重放后仍冲突则以 Faulted 终止会话，不允许带发散状态继续驱动 PLC。
    /// </summary>
    private async Task<bool> FlushAsync(LaneScope lane, CancellationToken ct, Action? replayEngineWrites = null)
    {
        if (!lane.Db.ChangeTracker.HasChanges())
            return true;

        try
        {
            await lane.Db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await ReloadTrackedAsync(lane, ct);

            if (IsTerminal(lane.Batch.Status))
            {
                _log.LogInformation("批次 {BatchId} 已被外部操作推进到 {Status}，lane 收敛退出", lane.Batch.Id, lane.Batch.Status);
                return false;
            }

            // 握手日志/报警等新增实体不受冲突影响：重放引擎写后一并落盘。
            replayEngineWrites?.Invoke();
            try
            {
                await lane.Db.SaveChangesAsync(ct);
                _log.LogInformation("批次 {BatchId} 写入并发冲突，重放引擎写后收敛成功", lane.Batch.Id);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                await ReloadTrackedAsync(lane, ct);
                if (IsTerminal(lane.Batch.Status))
                    return false;

                _log.LogWarning("批次 {BatchId} 重放后写入仍冲突，置为故障并终止会话", lane.Batch.Id);
                lane.Batch.Fault("ENGINE", "并发写入持续冲突，引擎已停止驱动本批次，请操作员确认后重新开始。");
                lane.Db.AuditLogs.Add(new AuditLog(null, "system", "batch.fault", "ProductionBatch",
                    lane.Batch.Id.ToString(), "ENGINE:flush-conflict-divergence-guard"));
                lane.Db.ProcessAlarms.Add(new ProcessAlarm(lane.Batch.Id, lane.Batch.BatchNo, lane.Batch.CurrentStepId,
                    "", "ENGINE", "Fault", "并发写入持续冲突，引擎已停止驱动本批次。", DateTimeOffset.UtcNow));
                try
                {
                    await lane.Db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    await ReloadTrackedAsync(lane, ct);
                }

                await _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "fault",
                    new { code = "ENGINE", message = "并发写入持续冲突，引擎已停止驱动本批次。" }), CancellationToken.None);
                return false;
            }
        }
    }

    private async Task ReloadTrackedAsync(LaneScope lane, CancellationToken ct)
    {
        foreach (var entry in lane.Db.ChangeTracker.Entries()
                     .Where(e => e.State is not EntityState.Added and not EntityState.Detached).ToList())
        {
            await entry.ReloadAsync(ct);
        }
    }

    private bool TryGetHold(Guid batchId, out string reason) =>
        _holdReasons.TryGetValue(batchId, out reason!);

    private void ConsumeHold(Guid batchId)
    {
        _holdReasons.TryRemove(batchId, out _);
        DeleteIntent(batchId, SchedulerIntentKinds.Hold);
    }

    private void DeleteIntent(Guid batchId, string kind)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var rows = await db.SchedulerIntents
                    .Where(i => i.BatchId == batchId && i.Kind == kind)
                    .ToListAsync();
                if (rows.Count == 0)
                    return;
                db.SchedulerIntents.RemoveRange(rows);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "删除调度意图 {Kind} 失败 {BatchId}", kind, batchId);
            }
        });
    }

    private static async Task CommandPlcHoldAsync(IPlcHandshakeClient plc, TimeSpan timeout, CancellationToken ct)
    {
        await plc.SetHostHoldAsync(true, ct);
        var deadline = DateTime.UtcNow + timeout;
        PlcInboundSignals signals;
        do
        {
            ct.ThrowIfCancellationRequested();
            signals = await plc.ReadSignalsAsync(ct);
            if (signals.PlcHeld)
                return;
            await Task.Delay(80, ct);
        } while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException("PLC 未在时限内以 PLC_Held 应答 Host_Hold。");
    }

    private static async Task ReleasePlcHoldAsync(IPlcHandshakeClient plc, TimeSpan timeout, CancellationToken ct)
    {
        await plc.SetHostHoldAsync(false, ct);
        var deadline = DateTime.UtcNow + timeout;
        PlcInboundSignals signals;
        do
        {
            ct.ThrowIfCancellationRequested();
            signals = await plc.ReadSignalsAsync(ct);
            if (!signals.PlcHeld)
                return;
            await Task.Delay(80, ct);
        } while (DateTime.UtcNow < deadline);
    }

    private void RecordHandshake(
        LaneScope lane,
        SnapshotStep step,
        HandshakeStateMachine machine,
        HandshakeWorkContext work,
        DateTimeOffset now,
        string kind,
        string? detail)
    {
        var tagged = string.IsNullOrWhiteSpace(lane.Equipment.Code) || string.IsNullOrWhiteSpace(detail)
            ? detail
            : $"[{lane.Equipment.Code}] {detail}";
        lane.Db.HandshakeEvents.Add(new HandshakeEvent(
            lane.Batch.Id, step.StepId, step.Code, machine.Phase.ToString(), kind, tagged,
            machine.RemainingSeconds(work, now)));
    }
}
