using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Materials;

public enum MaterialLotSource
{
    Received = 0,
    Produced = 1,
    Split = 2
}

public enum MaterialLotStatus
{
    Open = 0,
    Consumed = 1,
    Quarantine = 2,
    Released = 3,
    Void = 4
}

public enum MaterialUseRole
{
    Charge = 0,
    Produced = 1,
    Rework = 2
}

public sealed class MaterialLot : Entity
{
    public string LotNumber { get; private set; } = string.Empty;
    public string MaterialCode { get; private set; } = string.Empty;
    public string MaterialName { get; private set; } = string.Empty;
    public Guid? ParentLotId { get; private set; }
    public MaterialLotSource Source { get; private set; }
    public MaterialLotStatus Status { get; private set; } = MaterialLotStatus.Open;
    public double? Quantity { get; private set; }
    public string? Uom { get; private set; }
    public Guid? ProducedBatchId { get; private set; }

    private MaterialLot() { }

    public static MaterialLot Receive(string lotNumber, string materialCode, string materialName, double? quantity, string? uom)
    {
        return Create(lotNumber, materialCode, materialName, MaterialLotSource.Received, null, quantity, uom, null);
    }

    public static MaterialLot Produce(string lotNumber, string materialCode, string materialName, Guid batchId, double? quantity, string? uom)
    {
        return Create(lotNumber, materialCode, materialName, MaterialLotSource.Produced, null, quantity, uom, batchId);
    }

    public MaterialLot Split(string childLotNumber, double? childQuantity)
    {
        if (Status != MaterialLotStatus.Open)
            throw new DomainException("LOT_STATUS", $"批次 {LotNumber} 状态 {Status} 不能拆分。");
        if (string.IsNullOrWhiteSpace(childLotNumber))
            throw new DomainException("LOT_NUMBER", "拆分后的批次号不能为空。");
        if (childQuantity is double qty)
        {
            if (qty <= 0)
                throw new DomainException("LOT_QTY", "拆分数量必须大于 0。");
            if (Quantity is double remaining)
            {
                if (qty > remaining)
                    throw new DomainException("LOT_QTY", "拆分数量不能超过母批剩余量。");
                Quantity = remaining - qty;
            }
        }

        Touch();
        return Create(childLotNumber.Trim(), MaterialCode, MaterialName, MaterialLotSource.Split, Id, childQuantity, Uom, null);
    }

    public void MarkConsumed()
    {
        if (Status is MaterialLotStatus.Void or MaterialLotStatus.Quarantine)
            throw new DomainException("LOT_STATUS", $"批次 {LotNumber} 状态 {Status} 不能消耗。");
        Status = MaterialLotStatus.Consumed;
        Touch();
    }

    public void MarkReleased()
    {
        if (Status == MaterialLotStatus.Void)
            throw new DomainException("LOT_STATUS", $"批次 {LotNumber} 已作废。");
        Status = MaterialLotStatus.Released;
        Touch();
    }

    public void Quarantine()
    {
        if (Status == MaterialLotStatus.Void)
            throw new DomainException("LOT_STATUS", $"批次 {LotNumber} 已作废。");
        Status = MaterialLotStatus.Quarantine;
        Touch();
    }

    public void BindProducedBatch(Guid batchId)
    {
        ProducedBatchId ??= batchId;
        Touch();
    }

    private static MaterialLot Create(
        string lotNumber,
        string materialCode,
        string materialName,
        MaterialLotSource source,
        Guid? parentLotId,
        double? quantity,
        string? uom,
        Guid? producedBatchId)
    {
        if (string.IsNullOrWhiteSpace(lotNumber))
            throw new DomainException("LOT_NUMBER", "物料批次号不能为空。");
        if (string.IsNullOrWhiteSpace(materialCode))
            throw new DomainException("MATERIAL", "物料编码不能为空。");
        return new MaterialLot
        {
            LotNumber = lotNumber.Trim(),
            MaterialCode = materialCode.Trim(),
            MaterialName = string.IsNullOrWhiteSpace(materialName) ? materialCode.Trim() : materialName.Trim(),
            Source = source,
            ParentLotId = parentLotId,
            Quantity = quantity,
            Uom = string.IsNullOrWhiteSpace(uom) ? null : uom.Trim(),
            ProducedBatchId = producedBatchId,
            Status = MaterialLotStatus.Open
        };
    }
}

public sealed class BatchMaterialUse : Entity
{
    public Guid BatchId { get; private set; }
    public Guid MaterialLotId { get; private set; }
    public MaterialUseRole Role { get; private set; }
    public double? Quantity { get; private set; }
    public Guid? StepId { get; private set; }

    private BatchMaterialUse() { }

    public BatchMaterialUse(Guid batchId, Guid materialLotId, MaterialUseRole role, double? quantity = null, Guid? stepId = null)
    {
        BatchId = batchId;
        MaterialLotId = materialLotId;
        Role = role;
        Quantity = quantity;
        StepId = stepId;
    }
}

public static class MaterialGenealogy
{
    public static IReadOnlyList<MaterialLot> Ancestors(MaterialLot lot, IReadOnlyDictionary<Guid, MaterialLot> byId)
    {
        var chain = new List<MaterialLot>();
        var seen = new HashSet<Guid> { lot.Id };
        var current = lot.ParentLotId;
        while (current is Guid parentId && byId.TryGetValue(parentId, out var parent) && seen.Add(parent.Id))
        {
            chain.Add(parent);
            current = parent.ParentLotId;
        }

        chain.Reverse();
        return chain;
    }

    public static IReadOnlyList<MaterialLot> Descendants(Guid rootId, IReadOnlyList<MaterialLot> all)
    {
        var children = all.Where(l => l.ParentLotId == rootId).ToList();
        var result = new List<MaterialLot>();
        foreach (var child in children.OrderBy(c => c.LotNumber, StringComparer.Ordinal))
        {
            result.Add(child);
            result.AddRange(Descendants(child.Id, all));
        }

        return result;
    }
}
