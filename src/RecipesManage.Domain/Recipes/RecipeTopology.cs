using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Recipes;

public static class RecipeTopology
{
    public static void Validate(IReadOnlyCollection<RecipeStep> steps, IReadOnlyCollection<RecipeEdge> edges)
    {
        if (steps.Count == 0)
            return;

        var ids = steps.Select(s => s.Id).ToHashSet();
        foreach (var edge in edges)
        {
            if (!ids.Contains(edge.FromStepId) || !ids.Contains(edge.ToStepId))
                throw new DomainException("EDGE_ORPHAN", "存在指向未知工步的连线。");
            if (edge.FromStepId == edge.ToStepId)
                throw new DomainException("EDGE_SELF", "工步不能自环。");
        }

        ValidateUnitBoundaries(steps, edges);
        _ = Order(steps, edges);
    }

    /// <summary>
    /// Cross-unit edges must leave the last Phase of a Unit Procedure and enter the first Phase of the next.
    /// Intra-unit skip-into-the-middle would break ISA-88 unit boundaries on the PLC handshake stream.
    /// </summary>
    public static void ValidateUnitBoundaries(
        IReadOnlyCollection<RecipeStep> steps,
        IReadOnlyCollection<RecipeEdge> edges)
    {
        var byId = steps.ToDictionary(s => s.Id);
        foreach (var edge in edges)
        {
            var from = byId[edge.FromStepId];
            var to = byId[edge.ToStepId];
            var fromUnit = Isa88.UnitName(from.UnitProcedure);
            var toUnit = Isa88.UnitName(to.UnitProcedure);
            if (string.Equals(fromUnit, toUnit, StringComparison.Ordinal))
                continue;

            var fromHasIntraOut = edges.Any(other =>
                other.FromStepId == from.Id &&
                (other.ToStepId != edge.ToStepId || other.FromStepId != edge.FromStepId) &&
                string.Equals(Isa88.UnitName(byId[other.ToStepId].UnitProcedure), fromUnit, StringComparison.Ordinal));

            var toHasIntraIn = edges.Any(other =>
                other.ToStepId == to.Id &&
                (other.FromStepId != edge.FromStepId || other.ToStepId != edge.ToStepId) &&
                string.Equals(Isa88.UnitName(byId[other.FromStepId].UnitProcedure), toUnit, StringComparison.Ordinal));

            if (fromHasIntraOut)
                throw new DomainException("ISA88_UNIT",
                    $"跨单元连线必须从单元规程末工步出发：{from.Code} 仍有 {fromUnit} 内后续工步。");
            if (toHasIntraIn)
                throw new DomainException("ISA88_UNIT",
                    $"跨单元连线必须进入单元规程首工步：{to.Code} 在 {toUnit} 内已有前驱。");
        }
    }

    /// <summary>
    /// Collapses Phases into a Unit Procedure DAG and returns waves of units with no mutual predecessor.
    /// Units in the same wave may run in parallel when bound to different equipment; one PLC still serializes.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> UnitProcedureWaves(
        IReadOnlyCollection<RecipeStep> steps,
        IReadOnlyCollection<RecipeEdge> edges) =>
        UnitProcedureWaves(
            steps.Select(s => (s.Id, Isa88.UnitName(s.UnitProcedure))).ToList(),
            edges.Select(e => (e.FromStepId, e.ToStepId)).ToList());

    public static IReadOnlyList<IReadOnlyList<string>> UnitProcedureWaves(
        IReadOnlyCollection<(Guid Id, string Unit)> nodes,
        IReadOnlyCollection<(Guid From, Guid To)> edges)
    {
        if (nodes.Count == 0)
            return [];

        var byId = nodes.ToDictionary(s => s.Id);
        var units = nodes.Select(s => s.Unit).Distinct(StringComparer.Ordinal).ToList();
        var incoming = units.ToDictionary(u => u, _ => 0, StringComparer.Ordinal);
        var outgoing = units.ToDictionary(u => u, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            var fromUnit = byId[edge.From].Unit;
            var toUnit = byId[edge.To].Unit;
            if (string.Equals(fromUnit, toUnit, StringComparison.Ordinal))
                continue;
            if (outgoing[fromUnit].Add(toUnit))
                incoming[toUnit]++;
        }

        var remaining = units.ToHashSet(StringComparer.Ordinal);
        var waves = new List<IReadOnlyList<string>>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(u => incoming[u] == 0).OrderBy(u => u, StringComparer.Ordinal).ToList();
            if (ready.Count == 0)
                throw new DomainException("CYCLE", "单元规程拓扑存在环路。");
            waves.Add(ready);
            foreach (var unit in ready)
            {
                remaining.Remove(unit);
                foreach (var next in outgoing[unit])
                {
                    if (!remaining.Contains(next))
                        continue;
                    incoming[next]--;
                }
            }
        }

        return waves;
    }

    public static IReadOnlyList<RecipeStep> Order(IReadOnlyCollection<RecipeStep> steps, IReadOnlyCollection<RecipeEdge> edges)
    {
        if (steps.Count == 0)
            return [];

        var byId = steps.ToDictionary(s => s.Id);
        var incoming = steps.ToDictionary(s => s.Id, _ => 0);
        var outgoing = steps.ToDictionary(s => s.Id, _ => new List<Guid>());

        foreach (var edge in edges)
        {
            incoming[edge.ToStepId]++;
            outgoing[edge.FromStepId].Add(edge.ToStepId);
        }

        var queue = new Queue<RecipeStep>(
            steps.Where(s => incoming[s.Id] == 0).OrderBy(s => s.Ordinal).ThenBy(s => s.Code));
        var ordered = new List<RecipeStep>(steps.Count);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            ordered.Add(node);
            foreach (var nextId in outgoing[node.Id])
            {
                incoming[nextId]--;
                if (incoming[nextId] == 0)
                    queue.Enqueue(byId[nextId]);
            }
        }

        if (ordered.Count != steps.Count)
            throw new DomainException("CYCLE", "工艺拓扑存在环路，无法按顺序下发 PLC。");

        return ordered;
    }
}
