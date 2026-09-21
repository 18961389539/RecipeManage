namespace RecipesManage.Domain.Batches;

public sealed record EquipmentOccupant(
    Guid BatchId,
    string BatchNo,
    string Status,
    string HandshakePhase);

/// <summary>
/// 一台设备同时只允许一个运行/排队/保持批次占用（与 EQ_BUSY 一致）。
/// </summary>
public static class EquipmentOccupancy
{
    public static Dictionary<Guid, EquipmentOccupant> Index(
        IEnumerable<ProductionBatch> live,
        Func<ProductionBatch, IReadOnlyCollection<Guid>> boundEquipment)
    {
        var map = new Dictionary<Guid, EquipmentOccupant>();
        foreach (var batch in live.OrderBy(b => b.StartedAt ?? b.CreatedAt))
        {
            var occupant = new EquipmentOccupant(
                batch.Id, batch.BatchNo, batch.Status.ToString(), batch.HandshakePhase);
            foreach (var id in boundEquipment(batch))
                map.TryAdd(id, occupant);
        }

        return map;
    }
}
