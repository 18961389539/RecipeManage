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

        foreach (var parameter in step.Parameters.Where(p => p.ArchiveAsQuality).OrderBy(p => p.SlotIndex))
            result[parameter.Name] = Match(parameter, measured, used);

        foreach (var (tag, value) in measured)
            result[$"PLC:{tag}"] = value;

        return result;
    }

    private static double Match(
        SnapshotParameter parameter,
        IReadOnlyDictionary<string, double> measured,
        HashSet<string> used)
    {
        var unit = parameter.EngineeringUnit ?? "";
        var name = parameter.Name ?? "";

        if (LooksLikeDuration(name, unit) && TryTake(measured, used, "HoldTime", out var hold))
            return hold;
        if (LooksLikeTemperature(name, unit) && TryTake(measured, used, "Temperature", out var temp))
            return temp;
        if (LooksLikePressure(name, unit) && TryTake(measured, used, "Pressure", out var pressure))
            return pressure;

        return parameter.Setpoint;
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
        !LooksLikeDuration(name, unit) &&
        !LooksLikeHardness(name, unit) && (
            unit.Contains('℃', StringComparison.Ordinal) ||
            unit.Equals("C", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("温度", StringComparison.Ordinal) ||
            name.Contains("temp", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeHardness(string name, string unit) =>
        name.Contains("硬度", StringComparison.Ordinal) ||
        name.Contains("HB", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("HB", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("HRC", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeRate(string name, string unit) =>
        unit.Contains('/') ||
        name.Contains("斜率", StringComparison.Ordinal) ||
        name.Contains("ramp", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeDuration(string name, string unit) =>
        !LooksLikeRate(name, unit) && (
            unit.Equals("s", StringComparison.OrdinalIgnoreCase) ||
            unit.Equals("sec", StringComparison.OrdinalIgnoreCase) ||
            unit.Equals("min", StringComparison.OrdinalIgnoreCase) ||
            unit.Equals("h", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("时长", StringComparison.Ordinal) ||
            name.Contains("时间", StringComparison.Ordinal));

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
                continue;
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

public sealed record QualitySpecResult(string Name, double Value, double? Min, double? Max, bool OutOfSpec);
