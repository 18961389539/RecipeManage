using System.Text.Json;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Domain.Batches;

/// <summary>
/// 对照控制配方快照规格判定归档质检是否超差，供质量放行前拦截。
/// </summary>
public static class QualityDisposition
{
    public static bool HasOutOfSpec(ControlRecipeSnapshot snapshot, IEnumerable<BatchStepExecution> executions)
    {
        foreach (var exec in executions)
        {
            if (exec.Outcome is "Skipped" or "Faulted")
                continue;
            if (string.IsNullOrWhiteSpace(exec.QualityJson) || !exec.QualityJson.TrimStart().StartsWith('{'))
                continue;
            Dictionary<string, double>? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<Dictionary<string, double>>(exec.QualityJson);
            }
            catch (JsonException)
            {
                continue;
            }
            if (parsed is null)
                continue;

            var step = snapshot.Steps.FirstOrDefault(s => s.StepId == exec.StepId);
            if (step is null)
                continue;
            foreach (var (tag, value) in parsed)
            {
                if (tag.StartsWith("PLC:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var param = step.Parameters.FirstOrDefault(p =>
                    string.Equals(p.Name, tag, StringComparison.Ordinal));
                if (param is null)
                    continue;
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
