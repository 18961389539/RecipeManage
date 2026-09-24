using RecipesManage.Domain.Recipes;

namespace RecipesManage.Domain.Batches;

/// <summary>
/// 将 PLC 实测点绑定到工步上标记为 ArchiveAsQuality 的工艺参数名，避免归档只有 Temperature 这类驱动标签。
/// </summary>
public static class QualityArchive
{
    public static Dictionary<string, double> Bind(SnapshotStep step, IReadOnlyDictionary<string, double> measured)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archived = step.Parameters.Where(p => p.ArchiveAsQuality).OrderBy(p => p.SlotIndex).ToList();

        // 声明了实测点的参数先取，避免被同名推断抢走标签。
        foreach (var parameter in archived.Where(p => !string.IsNullOrWhiteSpace(p.MeasuredTag)))
        {
            if (TryTake(measured, used, parameter.MeasuredTag!, out var value))
                result[parameter.Name] = value;
        }

        foreach (var parameter in archived.Where(p => string.IsNullOrWhiteSpace(p.MeasuredTag)))
        {
            if (TryMatch(parameter, measured, used, out var value))
                result[parameter.Name] = value;
        }

        foreach (var (tag, value) in measured)
            result[$"PLC:{tag}"] = value;

        return result;
    }

    private static bool TryMatch(
        SnapshotParameter parameter,
        IReadOnlyDictionary<string, double> measured,
        HashSet<string> used,
        out double value)
    {
        var unit = parameter.EngineeringUnit ?? "";
        var name = parameter.Name ?? "";
        var semantic = ParameterSemantics.Resolve(parameter.Semantic, name, unit);

        if (semantic == ParameterSemantic.Duration && TryTake(measured, used, "HoldTime", out value))
            return true;
        if (semantic != ParameterSemantic.Duration &&
            !LooksLikeHardness(name, unit) &&
            LooksLikeTemperature(name, unit) &&
            TryTake(measured, used, "Temperature", out value))
            return true;
        if (LooksLikePressure(name, unit) && TryTake(measured, used, "Pressure", out value))
            return true;

        value = 0;
        return false;
    }

    private static bool TryTake(
        IReadOnlyDictionary<string, double> measured,
        HashSet<string> used,
        string tag,
        out double value)
    {
        if (!used.Contains(tag) && measured.TryGetValue(tag, out value))
        {
            used.Add(tag);
            return true;
        }

        value = 0;
        return false;
    }

    private static bool LooksLikeTemperature(string name, string unit) =>
        unit.Contains('℃', StringComparison.Ordinal) ||
        unit.Equals("C", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("温度", StringComparison.Ordinal) ||
        name.Contains("temp", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeHardness(string name, string unit) =>
        name.Contains("硬度", StringComparison.Ordinal) ||
        name.Contains("HB", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("HB", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("HRC", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePressure(string name, string unit) =>
        name.Contains("压", StringComparison.Ordinal) ||
        name.Contains("pressure", StringComparison.OrdinalIgnoreCase) ||
        unit.Contains("Pa", StringComparison.OrdinalIgnoreCase) ||
        unit.Contains("bar", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<QualitySpecResult> Evaluate(
        SnapshotStep step,
        IReadOnlyDictionary<string, double> archived)
    {
        var rows = new List<QualitySpecResult>();
        foreach (var parameter in step.Parameters.Where(p => p.ArchiveAsQuality).OrderBy(p => p.SlotIndex))
        {
            if (!archived.TryGetValue(parameter.Name, out var value))
            {
                rows.Add(new QualitySpecResult(parameter.Name, 0, parameter.Min, parameter.Max, false, true));
                continue;
            }
            var oos = (parameter.Min is { } min && value < min) || (parameter.Max is { } max && value > max);
            rows.Add(new QualitySpecResult(parameter.Name, value, parameter.Min, parameter.Max, oos));
        }

        return rows;
    }

    public static string FormatSetpointVsActual(double setpoint, QualitySpecResult? actual) =>
        actual is null
            ? setpoint.ToString("0.###")
            : $"{setpoint:0.###} / {actual.Value:0.##}{(actual.OutOfSpec ? " 超差" : "")}";

    public static IReadOnlyList<QualitySpecResult> EvaluateJson(SnapshotStep step, string? qualityJson)
    {
        if (string.IsNullOrWhiteSpace(qualityJson))
            return [];
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(qualityJson);
            return parsed is null ? [] : Evaluate(step, parsed);
        }
        catch
        {
            return [];
        }
    }
}

public sealed record QualitySpecResult(
    string Name,
    double Value,
    double? Min,
    double? Max,
    bool OutOfSpec,
    bool Unmeasured = false);
