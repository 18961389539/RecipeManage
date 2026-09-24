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
    /// 启动时对账：为仍占用的批次补写租约，清理终态残留。
    /// 同一设备多批时把租约转给优先级更高的占用者（运行 / 排队 / 保持 先于故障），
    /// 避免历史故障批把正在保持的批次从总览占用表上挤掉。
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var live = await _db.Batches
            .Where(b => b.Status == BatchStatus.Queued
                        || b.Status == BatchStatus.Running
                        || b.Status == BatchStatus.Held
                        || b.Status == BatchStatus.Faulted)
            .ToListAsync(ct);

        var claimants = new Dictionary<Guid, List<ProductionBatch>>();
        foreach (var batch in live)
        {
            foreach (var id in BatchService.BoundEquipmentIds(batch))
            {
                if (!claimants.TryGetValue(id, out var list))
                    claimants[id] = list = [];
                list.Add(batch);
            }
        }

        var codes = await _db.Equipment.AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Code, ct);
        var leases = await _db.EquipmentLeases.ToListAsync(ct);
        var byEquipment = leases.ToDictionary(l => l.EquipmentId);
        var dirty = false;

        foreach (var (equipmentId, contestants) in claimants)
        {
            var winner = EquipmentOccupancy.Preferred(contestants);
            var code = codes.GetValueOrDefault(equipmentId, equipmentId.ToString("N")[..8]);
            if (byEquipment.TryGetValue(equipmentId, out var lease))
            {
                if (lease.BatchId == winner.Id)
                    continue;
                _log.LogWarning(
                    "设备 {EquipmentCode} 租约从 {FromBatch} 转给 {ToBatch}（{Status}）",
                    code, lease.BatchNo, winner.BatchNo, winner.Status);
                lease.TransferTo(winner.Id, winner.BatchNo, DateTimeOffset.UtcNow);
                dirty = true;
                continue;
            }

            _db.EquipmentLeases.Add(new EquipmentLease(
                equipmentId, code, winner.Id, winner.BatchNo, DateTimeOffset.UtcNow));
            dirty = true;
        }

        if (dirty)
            await _db.SaveChangesAsync(ct);

        var liveIds = live.Select(b => b.Id).ToHashSet();
        var claimed = claimants.Keys.ToHashSet();
        var stale = await _db.EquipmentLeases
            .Where(l => !liveIds.Contains(l.BatchId) || !claimed.Contains(l.EquipmentId))
            .ToListAsync(ct);
        if (stale.Count > 0)
        {
            _db.EquipmentLeases.RemoveRange(stale);
            await _db.SaveChangesAsync(ct);
        }
    }
}
