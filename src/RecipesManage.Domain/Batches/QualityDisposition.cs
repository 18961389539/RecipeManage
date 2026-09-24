using System.Text.Json;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Domain.Batches;

/// <summary>
/// 对照控制配方快照规格判定归档质检是否超差，供质量放行前拦截。
/// 标记 ArchiveAsQuality 的参数必须有实测绑定；禁止用设定值或 PLC 原始通道冒充合格。
/// </summary>
public static class QualityDisposition
{
    public static bool HasOutOfSpec(ControlRecipeSnapshot snapshot, IEnumerable<BatchStepExecution> executions)
    {
        var byStep = executions.ToDictionary(e => e.StepId);
        foreach (var step in snapshot.Steps)
        {
            if (!byStep.TryGetValue(step.StepId, out var exec))
                continue;
            if (exec.Outcome is StepOutcome.Skipped or StepOutcome.Faulted or StepOutcome.Pending)
                continue;

            var qualityParams = step.Parameters.Where(p => p.ArchiveAsQuality).ToList();
            if (qualityParams.Count == 0)
                continue;

            if (string.IsNullOrWhiteSpace(exec.QualityJson) || !exec.QualityJson.TrimStart().StartsWith('{'))
                return true;

            Dictionary<string, double>? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<Dictionary<string, double>>(exec.QualityJson);
            }
            catch (JsonException)
            {
                return true;
            }
            if (parsed is null)
                return true;

            foreach (var param in qualityParams)
            {
                if (!parsed.TryGetValue(param.Name, out var value))
                    return true;
                if (param.Min is double min && value < min)
                    return true;
                if (param.Max is double max && value > max)
                    return true;
            }
        }

        return false;
    }

    public static bool HasFailedLabSample(IEnumerable<LabSample> samples) =>
        samples.Any(s => s.Disposition == LabSampleDisposition.Fail);

    public static bool HasPendingFinalSample(IEnumerable<LabSample> samples) =>
        samples.Any(s => s.SampleType == LabSampleType.Final && s.Disposition == LabSampleDisposition.Pending);
}
