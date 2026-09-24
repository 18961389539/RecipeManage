using RecipesManage.Domain.Batches;

namespace RecipesManage.Domain.Recipes;

/// <summary>
/// 从工步参数解析工艺时长（保温/等待），与斜率等速率参数区分。
/// 显式声明 <see cref="ParameterSemantic.Duration"/> 的参数优先于按名称/单位推断的参数。
/// 握手看门狗仍用 WatchdogSeconds；UI 剩余秒数应使用本值。
/// </summary>
public static class ProcessDuration
{
    public static TimeSpan? TryFrom(SnapshotStep step)
    {
        var ordered = step.Parameters.OrderBy(p => p.SlotIndex).ToList();

        foreach (var parameter in ordered.Where(p => p.Semantic == ParameterSemantic.Duration))
            return ToTimeSpan(parameter.Setpoint, parameter.EngineeringUnit);

        foreach (var parameter in ordered.Where(p => p.Semantic == ParameterSemantic.Unspecified))
        {
            if (ParameterSemantics.IsDuration(parameter.Name, parameter.EngineeringUnit))
                return ToTimeSpan(parameter.Setpoint, parameter.EngineeringUnit);
        }

        return null;
    }

    public static TimeSpan ToTimeSpan(double value, string unit)
    {
        if (unit.Equals("min", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMinutes(Math.Max(0, value));
        if (unit.Equals("h", StringComparison.OrdinalIgnoreCase) ||
            unit.Equals("hr", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromHours(Math.Max(0, value));
        return TimeSpan.FromSeconds(Math.Max(0.2, value));
    }
}
