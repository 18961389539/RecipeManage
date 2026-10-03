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
        public required IAppDbContext Db { get; init; }
        public required ProductionBatch Batch { get; init; }
        public required EquipmentLine Equipment { get; init; }
        public required IPlcHandshakeClient Plc { get; init; }
        public required ControlRecipeSnapshot Snapshot { get; init; }
        public required bool MultiLane { get; init; }
        public BatchLane? Row { get; set; }

        /// <summary>读通道的健康记账（<see cref="PlcLinkMonitor"/>）。</summary>
        public PlcLinkMonitor Link { get; } = new(DateTimeOffset.UtcNow);

        /// <summary>本设备的看门狗参数；RunLaneAsync 解析后写入，容忍窗口也在里面。</summary>
        public HandshakeWatchdogOptions Watchdog { get; set; } = new();

        public async ValueTask DisposeAsync() => await Scope.DisposeAsync();
    }

    // 终态名单唯一来源在域层，这里只是给按 status 取值的调用点留个简写。
    private static bool IsTerminal(BatchStatus status) => ProductionBatch.IsTerminalState(status);

    private async Task RunUnitWavesAsync(Guid batchId, CancellationToken ct)
    {
        using var bootScope = _scopes.CreateScope();
        var boot = bootScope.ServiceProvider.GetRequiredService<IAppDbContext>();

        var batch = await boot.Batches.Include(b => b.StepExecutions).FirstOrDefaultAsync(b => b.Id == batchId, ct)
                    ?? throw new InvalidOperationException("批次不存在");
        if (batch.Status is BatchStatus.Held or BatchStatus.Aborted or BatchStatus.Completed
             or BatchStatus.Released or BatchStatus.DispositionRejected)
            return;

        var snapshot = SnapshotJson.Deserialize(batch.ControlRecipeJson)
                       ?? throw new InvalidOperationException("快照损坏");
        // 快照比本程序新（升级后又回退了程序）：不按旧形状去驱动 PLC，会话会把批次置成 ENGINE 故障并写明原因。
        SnapshotSchema.DemandSupported(snapshot);
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
                // 一次网络抖动不该在批次还没动手之前就把它判死：连接可重试（幂等），写不行。
                var client = await PlcConnectRetry.ConnectAsync(() => _drivers.Create(eq), eq, _log, ct);
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
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var status = await db.Batches.AsNoTracking()
            .Where(b => b.Id == batchId)
            .Select(b => b.Status)
            .FirstOrDefaultAsync(ct);
        return IsTerminal(status);
    }

    private async Task MarkBatchCompletedAsync(Guid batchId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
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
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var existing = await db.Lanes.Where(l => l.BatchId == batchId).Select(l => l.EquipmentId).ToListAsync(ct);

        foreach (var equipment in equipmentRows)
        {
            if (existing.Contains(equipment.Id))
                continue;
            db.Lanes.Add(new BatchLane(batchId, equipment.Id, equipment.Code, string.Empty));
        }

        await db.SaveChangesAsync(ct);
    }
}
