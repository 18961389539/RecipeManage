using RecipesManage.Domain.Equipment;

namespace RecipesManage.Domain.Batches;

public sealed record EquipmentOccupant(
    Guid BatchId,
    string BatchNo,
    string Status,
    string HandshakePhase);

/// <summary>
/// 占用投影：租约与仍占用的批次都是候选人；同一设备多批时取
/// <see cref="EquipmentLeasePolicy.OccupancyRank"/> 更高者，而不是谁先写入租约。
/// </summary>
public static class EquipmentOccupancy
{
    public static Dictionary<Guid, EquipmentOccupant> Index(
        IEnumerable<ProductionBatch> live,
        Func<ProductionBatch, IReadOnlyCollection<Guid>> boundEquipment,
        IEnumerable<EquipmentLease>? leases = null)
    {
        var batches = live.ToDictionary(b => b.Id);
        var claimants = new Dictionary<Guid, List<ProductionBatch>>();

        void consider(Guid equipmentId, ProductionBatch batch)
        {
            if (!claimants.TryGetValue(equipmentId, out var list))
                claimants[equipmentId] = list = [];
            if (list.Exists(b => b.Id == batch.Id))
                return;
            list.Add(batch);
        }

        foreach (var lease in leases ?? [])
        {
            if (batches.TryGetValue(lease.BatchId, out var batch))
                consider(lease.EquipmentId, batch);
        }

        foreach (var batch in live)
        {
            foreach (var id in boundEquipment(batch))
                consider(id, batch);
        }

        return claimants.ToDictionary(
            kv => kv.Key,
            kv => OccupantOf(Preferred(kv.Value)));
    }

    public static ProductionBatch Preferred(IReadOnlyList<ProductionBatch> claimants)
    {
        if (claimants.Count == 0)
            throw new ArgumentException("占用候选人不能为空。", nameof(claimants));
        return claimants
            .OrderBy(b => EquipmentLeasePolicy.OccupancyRank(b.Status))
            .ThenBy(b => b.StartedAt ?? b.CreatedAt)
            .First();
    }

    private static EquipmentOccupant OccupantOf(ProductionBatch batch) =>
        new(batch.Id, batch.BatchNo, batch.Status.ToString(), batch.HandshakePhase);
}
