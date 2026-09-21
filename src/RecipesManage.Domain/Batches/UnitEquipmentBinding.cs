using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Domain.Batches;

public static class UnitEquipmentBinding
{
    public static IReadOnlyDictionary<string, Guid>? Normalize(
        IReadOnlyDictionary<string, Guid>? requested,
        IReadOnlyCollection<string> units,
        Guid primaryEquipmentId)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var unit in units.Select(Isa88.UnitName).Distinct(StringComparer.Ordinal))
            map[unit] = primaryEquipmentId;

        if (requested is not null)
        {
            foreach (var (rawKey, equipmentId) in requested)
            {
                var unit = Isa88.UnitName(rawKey);
                if (!map.ContainsKey(unit))
                    throw new DomainException("UNIT_EQ", $"单元规程 {unit} 不在本配方中，不能绑定设备。");
                if (equipmentId != Guid.Empty)
                    map[unit] = equipmentId;
            }
        }

        if (map.Count == 0 || map.Values.All(id => id == primaryEquipmentId))
            return null;
        return map;
    }

    public static Guid Resolve(ControlRecipeSnapshot snapshot, string? unitProcedure, Guid primaryEquipmentId)
    {
        var unit = Isa88.UnitName(unitProcedure);
        if (snapshot.UnitEquipment is not null &&
            snapshot.UnitEquipment.TryGetValue(unit, out var id) &&
            id != Guid.Empty)
            return id;
        return primaryEquipmentId;
    }

    public static IReadOnlyList<Guid> AllIds(ControlRecipeSnapshot? snapshot, Guid primaryEquipmentId)
    {
        var ids = new HashSet<Guid> { primaryEquipmentId };
        if (snapshot?.UnitEquipment is null)
            return ids.ToList();
        foreach (var id in snapshot.UnitEquipment.Values)
        {
            if (id != Guid.Empty)
                ids.Add(id);
        }
        return ids.ToList();
    }
}
