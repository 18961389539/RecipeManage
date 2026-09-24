using System.Globalization;

namespace RecipesManage.Domain.Recipes;

public sealed record RecipeFieldChange(string Path, string? Before, string? After);

public sealed class RecipeGraphDiff
{
    public required int FromVersion { get; init; }
    public required int ToVersion { get; init; }
    public required IReadOnlyList<string> AddedSteps { get; init; }
    public required IReadOnlyList<string> RemovedSteps { get; init; }
    public required IReadOnlyList<RecipeFieldChange> Changes { get; init; }
}

public static class RecipeVersionComparer
{
    public static RecipeGraphDiff Compare(RecipeVersion from, RecipeVersion to)
    {
        var fromSteps = from.Steps.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
        var toSteps = to.Steps.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
        var added = toSteps.Keys.Except(fromSteps.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var removed = fromSteps.Keys.Except(toSteps.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var changes = new List<RecipeFieldChange>();
        Add(changes, "changeNote", from.ChangeNote ?? "", to.ChangeNote ?? "");

        foreach (var code in fromSteps.Keys.Intersect(toSteps.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            var a = fromSteps[code];
            var b = toSteps[code];
            Add(changes, $"{code}.name", a.Name, b.Name);
            Add(changes, $"{code}.type", a.Type.ToString(), b.Type.ToString());
            Add(changes, $"{code}.plcProgramId",
                a.PlcProgramId?.ToString(CultureInfo.InvariantCulture) ?? "",
                b.PlcProgramId?.ToString(CultureInfo.InvariantCulture) ?? "");
            Add(changes, $"{code}.watchdogSeconds", a.WatchdogSeconds.ToString(CultureInfo.InvariantCulture),
                b.WatchdogSeconds.ToString(CultureInfo.InvariantCulture));
            Add(changes, $"{code}.description", a.Description ?? "", b.Description ?? "");
            Add(changes, $"{code}.unitProcedure", a.UnitProcedure ?? "", b.UnitProcedure ?? "");
            Add(changes, $"{code}.operation", a.Operation ?? "", b.Operation ?? "");
            Add(changes, $"{code}.equipmentClassCode", a.EquipmentClassCode ?? "", b.EquipmentClassCode ?? "");

            var fromParams = a.Parameters.ToDictionary(p => p.SlotIndex);
            var toParams = b.Parameters.ToDictionary(p => p.SlotIndex);
            foreach (var slot in fromParams.Keys.Union(toParams.Keys).OrderBy(x => x))
            {
                fromParams.TryGetValue(slot, out var pa);
                toParams.TryGetValue(slot, out var pb);
                var prefix = $"{code}.parameters[{slot}]";
                Add(changes, $"{prefix}.name", pa?.Name, pb?.Name);
                Add(changes, $"{prefix}.unit", pa?.EngineeringUnit, pb?.EngineeringUnit);
                Add(changes, $"{prefix}.setpoint", Format(pa?.Setpoint), Format(pb?.Setpoint));
                Add(changes, $"{prefix}.min", Format(pa?.Min), Format(pb?.Min));
                Add(changes, $"{prefix}.max", Format(pa?.Max), Format(pb?.Max));
                Add(changes, $"{prefix}.writeToPlc", pa?.WriteToPlc.ToString(), pb?.WriteToPlc.ToString());
                Add(changes, $"{prefix}.archiveAsQuality", pa?.ArchiveAsQuality.ToString(), pb?.ArchiveAsQuality.ToString());
                Add(changes, $"{prefix}.scaleWithBatch", pa?.ScaleWithBatch.ToString(), pb?.ScaleWithBatch.ToString());
            }
        }

        var fromEdges = EdgeKeys(from);
        var toEdges = EdgeKeys(to);
        foreach (var edge in toEdges.Except(fromEdges).OrderBy(x => x))
            changes.Add(new RecipeFieldChange("edge.add", null, edge));
        foreach (var edge in fromEdges.Except(toEdges).OrderBy(x => x))
            changes.Add(new RecipeFieldChange("edge.remove", edge, null));

        return new RecipeGraphDiff
        {
            FromVersion = from.VersionNumber,
            ToVersion = to.VersionNumber,
            AddedSteps = added,
            RemovedSteps = removed,
            Changes = changes
        };
    }

    public static IReadOnlySet<string> ChangedStepCodes(RecipeGraphDiff diff)
    {
        var codes = new HashSet<string>(diff.AddedSteps, StringComparer.OrdinalIgnoreCase);
        foreach (var change in diff.Changes)
        {
            var path = change.Path;
            if (path.StartsWith("edge.", StringComparison.OrdinalIgnoreCase))
                continue;
            var dot = path.IndexOf('.');
            if (dot <= 0)
                continue;
            var code = path[..dot];
            if (!string.IsNullOrWhiteSpace(code))
                codes.Add(code);
        }

        return codes;
    }

    private static HashSet<string> EdgeKeys(RecipeVersion version)
    {
        var byId = version.Steps.ToDictionary(s => s.Id);
        return version.Edges
            .Select(e =>
            {
                byId.TryGetValue(e.FromStepId, out var from);
                byId.TryGetValue(e.ToStepId, out var to);
                return $"{from?.Code ?? e.FromStepId.ToString()}->{to?.Code ?? e.ToStepId.ToString()}";
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void Add(List<RecipeFieldChange> changes, string path, string? before, string? after)
    {
        before ??= "";
        after ??= "";
        if (!string.Equals(before, after, StringComparison.Ordinal))
            changes.Add(new RecipeFieldChange(path, before, after));
    }

    private static string Format(double? value) =>
        value is null ? "" : value.Value.ToString("G", CultureInfo.InvariantCulture);
}
