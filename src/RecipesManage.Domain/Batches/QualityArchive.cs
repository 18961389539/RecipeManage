using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Domain.Batches;

/// <summary>
/// 将 PLC 实测点绑定到工步上标记为 ArchiveAsQuality 的工艺参数名，避免归档只有 Temperature 这类驱动标签。
/// </summary>
public static class QualityArchive
{
    /// <summary>
    /// 这条参数的归档值从哪个实测点来。<c>null</c> 表示<strong>没有任何来源</strong>——
    /// 它会被 <see cref="QualityDisposition"/> 当成"未归档"判超差，而界面上看起来只是又一次质量偏差。
    /// 声明的 MeasuredTag 优先；推断只服务历史配方与已密封快照。
    /// </summary>
    public static string? ResolveSourceTag(
        string? name, string? unit, ParameterSemantic semantic, string? measuredTag) =>
        !string.IsNullOrWhiteSpace(measuredTag) ? measuredTag.Trim() : InferSourceTag(name, unit, semantic);

    /// <summary>
    /// 按语义/名称推断。关键词表是热处理时代留下的：非热处工艺（流量、pH、扭矩、厚度）在这里
    /// 一律推不出来，所以新配方必须显式声明 <see cref="ParameterSemantic.MeasuredValue"/> + MeasuredTag，
    /// 并由 <see cref="DemandArchivableSources"/> 在提交审核时把关。
    /// </summary>
    public static string? InferSourceTag(string? name, string? unit, ParameterSemantic semantic)
    {
        var role = ParameterSemantics.Resolve(semantic, name, unit);
        // 声明成实测值却没给点：不能靠猜补一个来源，宁可为空让校验报错。
        if (role == ParameterSemantic.MeasuredValue) return null;
        if (role == ParameterSemantic.Duration) return "HoldTime";
        // 硬度这类实验室量故意不从 PLC 通道冒充：它该走质检样品。
        if (LooksLikeHardness(name, unit)) return null;
        if (LooksLikeTemperature(name, unit)) return "Temperature";
        if (LooksLikePressure(name, unit)) return "Pressure";
        return null;
    }

    public static string? ResolveSourceTag(SnapshotParameter parameter) =>
        ResolveSourceTag(parameter.Name, parameter.EngineeringUnit, parameter.Semantic, parameter.MeasuredTag);

    /// <summary>
    /// 提交审核前的把关：标了归档、却没有任何实测来源的参数。
    /// 以前这里什么都不说——参数一路静默归档不到值，到放行时才以"超差"的面目出现，
    /// 而质量只要随手写一句意见就能把它糊过去（本仓库演示库里三条已放行批次就是这么过去的）。
    /// 实验室指标请建质检样品（LIMS），不要把 PLC 参数标成归档。
    /// </summary>
    public static void DemandArchivableSources(IEnumerable<Recipes.RecipeStep> steps)
    {
        var homeless = (from step in steps
                        from p in step.Parameters
                        where p.ArchiveAsQuality
                        where ResolveSourceTag(p.Name, p.EngineeringUnit, p.Semantic, p.MeasuredTag) is null
                        orderby step.Ordinal, p.SlotIndex
                        select $"工步 {step.Code}（{step.Name}）的参数「{p.Name}{(string.IsNullOrWhiteSpace(p.EngineeringUnit) ? "" : $" [{p.EngineeringUnit}]")}」")
            .ToList();
        if (homeless.Count == 0) return;

        throw new DomainException("QUALITY_SOURCE",
            $"以下参数标记了\u201c归档作质量判定\u201d，但没有任何实测来源（既没声明实测点，也不在能推断的量纲里）：" +
            $"{string.Join("、", homeless)}。请为它们填写实测点（对应设备点表的 Measured 键）；" +
            "硬度这类实验室指标请改建质检样品（LIMS），不要标归档。" +
            "留着不管的代价不是报错，而是每条批次都被判超差、要质量写一句偏差意见才能放行。");
    }

    public static Dictionary<string, double> Bind(SnapshotStep step, IReadOnlyDictionary<string, double> measured)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archived = step.Parameters.Where(p => p.ArchiveAsQuality).OrderBy(p => p.SlotIndex).ToList();

        // 声明了实测点的参数先取，避免被同名推断抢走标签。
        foreach (var parameter in archived.Where(p => !string.IsNullOrWhiteSpace(p.MeasuredTag)))
        {
            if (TryTake(measured, used, parameter.MeasuredTag!.Trim(), out var value))
                result[parameter.Name] = value;
        }

        foreach (var parameter in archived.Where(p => string.IsNullOrWhiteSpace(p.MeasuredTag)))
        {
            if (InferSourceTag(parameter.Name, parameter.EngineeringUnit, parameter.Semantic) is not { } tag)
                continue;
            if (TryTake(measured, used, tag, out var value))
                result[parameter.Name] = value;
        }

        foreach (var (tag, value) in measured)
            result[$"PLC:{tag}"] = value;

        return result;
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

    private static bool LooksLikeTemperature(string? name, string? unit)
    {
        var n = name ?? string.Empty;
        var u = unit ?? string.Empty;
        return u.Contains('℃', StringComparison.Ordinal) ||
               u.Equals("C", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("温度", StringComparison.Ordinal) ||
               n.Contains("temp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeHardness(string? name, string? unit)
    {
        var n = name ?? string.Empty;
        var u = unit ?? string.Empty;
        return n.Contains("硬度", StringComparison.Ordinal) ||
               n.Contains("HB", StringComparison.OrdinalIgnoreCase) ||
               u.Equals("HB", StringComparison.OrdinalIgnoreCase) ||
               u.Equals("HRC", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePressure(string? name, string? unit)
    {
        var n = name ?? string.Empty;
        var u = unit ?? string.Empty;
        return n.Contains("压", StringComparison.Ordinal) ||
               n.Contains("pressure", StringComparison.OrdinalIgnoreCase) ||
               u.Contains("Pa", StringComparison.OrdinalIgnoreCase) ||
               u.Contains("bar", StringComparison.OrdinalIgnoreCase);
    }

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
