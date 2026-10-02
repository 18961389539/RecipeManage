using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Equipment;

namespace RecipesManage.Application.Services;

/// <summary>
/// 设备占用快照：租约与占用中批次都是候选人，同设备多批时运行/排队/保持优先于故障。通过 SignalR execution/occupancy 推到总览。
/// </summary>
public static class OccupancyRealtime
{
    public static IReadOnlyList<EquipmentOccupancyDto> Snapshot(
        IEnumerable<EquipmentLine> equipment,
        IEnumerable<ProductionBatch> live,
        IEnumerable<EquipmentLease>? leases = null)
    {
        var occ = EquipmentOccupancy.Index(live, SnapshotJson.BoundEquipmentIds, leases);
        return equipment
            .OrderBy(e => e.Code)
            .Select(e =>
            {
                occ.TryGetValue(e.Id, out var occupant);
                return new EquipmentOccupancyDto(
                    e.Id, e.Code, e.Name, e.Protocol.ToString(), e.Enabled,
                    occupant is null ? "Idle" : "Occupied",
                    occupant?.BatchId, occupant?.BatchNo, occupant?.Status, occupant?.HandshakePhase);
            })
            .ToList();
    }

    public static async Task PublishAsync(
        IAppDbContext db,
        IExecutionPublisher publisher,
        Guid batchId,
        CancellationToken ct)
    {
        var live = await db.Batches.AsNoTracking()
            .Where(b => b.Status == BatchStatus.Queued
                        || b.Status == BatchStatus.Running
                        || b.Status == BatchStatus.Held
                        || b.Status == BatchStatus.Faulted)
            .ToListAsync(ct);
        var leases = await db.EquipmentLeases.AsNoTracking().ToListAsync(ct);
        var equipment = await db.Equipment.AsNoTracking().ToListAsync(ct);
        await publisher.PublishAsync(
            new ExecutionEvent(batchId, "occupancy", Snapshot(equipment, live, leases)), ct);
    }
}
