namespace RecipesManage.Domain.Recipes;

/// <summary>
/// 参数语义判定：显式声明优先，未声明时按名称与单位推断。
/// 推断分支只服务于历史配方与已密封快照，新建参数应显式声明 <c>Semantic</c>，
/// 这样换工艺（非热处理）时不再依赖中文关键词猜测。
/// </summary>
public static class ParameterSemantics
{
    public static ParameterSemantic Resolve(ParameterSemantic declared, string? name, string? unit) =>
        declared == ParameterSemantic.Unspecified ? Infer(name, unit) : declared;

    public static ParameterSemantic Infer(string? name, string? unit)
    {
        if (IsRate(name, unit))
            return ParameterSemantic.Rate;
        return IsDuration(name, unit) ? ParameterSemantic.Duration : ParameterSemantic.Unspecified;
    }

    /// <summary>速率参数（斜率、流量）永远不是工艺时长，即使单位里带 min。</summary>
    public static bool IsRate(string? name, string? unit)
    {
        var n = name ?? string.Empty;
        var u = unit ?? string.Empty;
        return u.Contains('/') ||
               n.Contains("斜率", StringComparison.Ordinal) ||
               n.Contains("ramp", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDuration(string? name, string? unit)
    {
        if (IsRate(name, unit))
            return false;
        var n = name ?? string.Empty;
        var u = unit ?? string.Empty;
        return u.Equals("s", StringComparison.OrdinalIgnoreCase) ||
               u.Equals("sec", StringComparison.OrdinalIgnoreCase) ||
               // 这组 token 必须与 ProcessDuration.ToTimeSpan 认的单位一致：这里少认一个，
               // 那条时长就既不算时长、也不换算，看门狗与"剩余秒数"双双失真。
               u.Equals("ms", StringComparison.OrdinalIgnoreCase) ||
               u.Equals("min", StringComparison.OrdinalIgnoreCase) ||
               u.Equals("h", StringComparison.OrdinalIgnoreCase) ||
               u.Equals("hr", StringComparison.OrdinalIgnoreCase) ||
               u.Equals("d", StringComparison.OrdinalIgnoreCase) ||
               u.Equals("day", StringComparison.OrdinalIgnoreCase) ||
               u.Equals("days", StringComparison.OrdinalIgnoreCase) ||
               u.Equals("毫秒", StringComparison.Ordinal) ||
               u.Equals("分钟", StringComparison.Ordinal) ||
               u.Equals("小时", StringComparison.Ordinal) ||
               u.Equals("天", StringComparison.Ordinal) ||
               n.Contains("时长", StringComparison.Ordinal) ||
               n.Contains("时间", StringComparison.Ordinal) ||
               n.Contains("等待", StringComparison.Ordinal);
    }
}
