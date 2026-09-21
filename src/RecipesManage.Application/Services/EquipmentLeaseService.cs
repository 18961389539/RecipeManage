using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;

namespace RecipesManage.Application.Services;

/// <summary>
/// 设备占用租约。排他性由 <c>equipment_leases.EquipmentId</c> 的唯一索引保证 ——
/// 插入即占用，冲突即被占用。替换原先 "SELECT 查一下有没有活跃批次再写入" 的
/// check-then-act 模式：后者在两名操作员同时点启动时会让两个批次去写同一台 PLC。
/// 调度引擎本身不申请租约（集成测试会绕过 BatchService 直接驱动调度），只负责释放。
/// </summary>
public sealed class EquipmentLeaseService
{
    private readonly IAppDbContext _db;
    private readonly ILogger<EquipmentLeaseService> _log;

    public EquipmentLeaseService(IAppDbContext db, ILogger<EquipmentLeaseService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>申请占用批次的全部绑定设备；任一设备被占则整批不启动。</summary>
    public async Task AcquireAsync(
        ProductionBatch batch,
        IReadOnlyCollection<Guid> equipmentIds,
        IReadOnlyDictionary<Guid, string> equipmentCodes,
        CancellationToken ct = default)
    {
        if (equipmentIds.Count == 0)
            return;

        var mine = await _db.EquipmentLeases
            .Where(l => l.BatchId == batch.Id)
            .Select(l => l.EquipmentId)
            .ToListAsync(ct);

        var pending = equipmentIds.Where(id => !mine.Contains(id)).ToList();
        if (pending.Count == 0)
            return;

        // 先查一次只是为了让"设备已被占用"走干净的业务异常，而不是把唯一索引冲突刷成
        // EF 的 fail 级日志。真正的排他保证始终来自唯一索引：并发下后到者必定命中冲突。
        var taken = await _db.EquipmentLeases
            .Where(l => pending.Contains(l.EquipmentId) && l.BatchId != batch.Id)
            .Select(l => l.EquipmentCode)
            .ToListAsync(ct);
        if (taken.Count > 0)
            throw new DomainException("EQ_BUSY", $"绑定设备已有批次在执行、排队或保持（{string.Join("、", taken)}）。");

        foreach (var id in pending)
            _db.EquipmentLeases.Add(new EquipmentLease(
                id, equipmentCodes.GetValueOrDefault(id, id.ToString("N")[..8]), batch.Id, batch.BatchNo, DateTimeOffset.UtcNow));

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // 唯一索引冲突是这里唯一可能的失败原因：设备已被别的批次租走。
            throw new DomainException("EQ_BUSY", "绑定设备已有批次在执行、排队或保持。");
        }
    }

    /// <summary>释放批次占用的全部设备（幂等，终态或中止均可多次调用）。</summary>
    public async Task ReleaseAsync(Guid batchId, CancellationToken ct = default)
    {
        var leases = await _db.EquipmentLeases.Where(l => l.BatchId == batchId).ToListAsync(ct);
        if (leases.Count == 0)
            return;

        _db.EquipmentLeases.RemoveRange(leases);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 并发释放说明已经释放过了。
        }
    }

    /// <summary>
    /// 启动时对账：为升级前就在跑的批次补写租约，清理已进入终态批次的残留租约。
    /// 顺序按批次时间，先到先得，后到者放弃（保留现状，不强行中止）。
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var live = await _db.Batches
            .Where(b => b.Status == BatchStatus.Queued
                        || b.Status == BatchStatus.Running
                        || b.Status == BatchStatus.Held
                        || b.Status == BatchStatus.Faulted)
            .ToListAsync(ct);

        foreach (var batch in live.OrderBy(b => b.StartedAt ?? b.CreatedAt))
        {
            var ids = BatchService.BoundEquipmentIds(batch);
            var codes = await _db.Equipment
                .Where(e => ids.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.Code, ct);
            try
            {
                await AcquireAsync(batch, ids, codes, ct);
            }
            catch (DomainException)
            {
                _log.LogWarning("批次 {BatchNo} 启动时未能补齐设备租约，设备可能已被其它批次占用", batch.BatchNo);
            }
        }

        var liveIds = live.Select(b => b.Id).ToHashSet();
        var stale = await _db.EquipmentLeases.Where(l => !liveIds.Contains(l.BatchId)).ToListAsync(ct);
        if (stale.Count > 0)
        {
            _db.EquipmentLeases.RemoveRange(stale);
            await _db.SaveChangesAsync(ct);
        }
    }
}
