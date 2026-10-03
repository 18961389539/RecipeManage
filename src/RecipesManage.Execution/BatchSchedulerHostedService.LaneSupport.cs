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
/// 车道循环共用的落库与 PLC 辅助：车道相位、带乐观并发的批次落库（<see cref="FlushAsync"/>）、
/// Host_Hold 握手、握手履历。
/// </summary>
public sealed partial class BatchSchedulerHostedService
{
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
                    "", "ENGINE", "Fault", "并发写入持续冲突，引擎已停止驱动本批次。", Clock.GetUtcNow()));
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
            // 已消费的意图不能随重载复活：内存里的请求已经取走，库里那行若被重载成 Unchanged，
            // 重启后会把执行过的跳步 / 确认再执行一遍。行已不在就不必再删。
            if (entry is { State: EntityState.Deleted, Entity: SchedulerIntent })
            {
                if (await entry.GetDatabaseValuesAsync(ct) is null)
                    entry.State = EntityState.Detached;
                continue;
            }
            await entry.ReloadAsync(ct);
        }
    }

    private async Task CommandPlcHoldAsync(IPlcHandshakeClient plc, TimeSpan timeout, CancellationToken ct)
    {
        await plc.SetHostHoldAsync(true, ct);
        var deadline = Clock.GetUtcNow().UtcDateTime + timeout;
        PlcInboundSignals signals;
        do
        {
            ct.ThrowIfCancellationRequested();
            signals = await plc.ReadSignalsAsync(ct);
            if (signals.PlcHeld)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(80), Clock, ct);
        } while (Clock.GetUtcNow().UtcDateTime < deadline);

        throw new InvalidOperationException("PLC 未在时限内以 PLC_Held 应答 Host_Hold。");
    }

    private async Task ReleasePlcHoldAsync(IPlcHandshakeClient plc, TimeSpan timeout, CancellationToken ct)
    {
        await plc.SetHostHoldAsync(false, ct);
        var deadline = Clock.GetUtcNow().UtcDateTime + timeout;
        PlcInboundSignals signals;
        do
        {
            ct.ThrowIfCancellationRequested();
            signals = await plc.ReadSignalsAsync(ct);
            if (!signals.PlcHeld)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(80), Clock, ct);
        } while (Clock.GetUtcNow().UtcDateTime < deadline);
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
