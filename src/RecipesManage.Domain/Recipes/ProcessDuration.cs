using RecipesManage.Domain.Batches;

namespace RecipesManage.Domain.Recipes;

/// <summary>
/// 从工步参数解析工艺时长（保温/等待），与斜率等速率参数区分。
/// 握手看门狗仍用 WatchdogSeconds；UI 剩余秒数应使用本值。
/// </summary>
public static class ProcessDuration
{
    public static TimeSpan? TryFrom(SnapshotStep step)
    {
        foreach (var parameter in step.Parameters.OrderBy(p => p.SlotIndex))
        {
            var name = parameter.Name ?? "";
            var unit = parameter.EngineeringUnit ?? "";
            if (IsRate(name, unit) || !IsDuration(name, unit))
                continue;
            return ToTimeSpan(parameter.Setpoint, unit);
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

    private static bool IsRate(string name, string unit) =>
        unit.Contains('/') ||
        name.Contains("斜率", StringComparison.Ordinal) ||
        name.Contains("ramp", StringComparison.OrdinalIgnoreCase);

    private static bool IsDuration(string name, string unit) =>
        unit.Equals("s", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("sec", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("min", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("h", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("hr", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("时长", StringComparison.Ordinal) ||
        name.Contains("时间", StringComparison.Ordinal) ||
        name.Contains("等待", StringComparison.Ordinal);
}
