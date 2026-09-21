using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Equipment;

namespace RecipesManage.Application.Services;

/// <summary>
/// 设备占用快照：排队/运行/保持占用，完成/中止/故障释放。通过 SignalR execution/occupancy 推到总览，禁止双批盲写。
/// </summary>
public static class OccupancyRealtime
{
    public static IReadOnlyList<EquipmentOccupancyDto> Snapshot(
        IEnumerable<EquipmentLine> equipment,
        IEnumerable<ProductionBatch> live)
    {
        var occ = EquipmentOccupancy.Index(live, BatchService.BoundEquipmentIds);
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
        // Faulted 也计入占用：故障批次仍压在设备上，必须操作员介入后才能释放
        // （与 EquipmentLeasePolicy / 设备租约一致）。
        var live = await db.Batches.AsNoTracking()
            .Where(b => b.Status == BatchStatus.Queued
                        || b.Status == BatchStatus.Running
                        || b.Status == BatchStatus.Held
                        || b.Status == BatchStatus.Faulted)
            .ToListAsync(ct);
        var equipment = await db.Equipment.AsNoTracking().ToListAsync(ct);
        await publisher.PublishAsync(
            new ExecutionEvent(batchId, "occupancy", Snapshot(equipment, live)), ct);
    }
}
