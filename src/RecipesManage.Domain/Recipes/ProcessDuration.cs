using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Recipes;

/// <summary>
/// 从工步参数解析工艺时长（保温/等待），与斜率等速率参数区分。
/// 显式声明 <see cref="ParameterSemantic.Duration"/> 的参数优先于按名称/单位推断的参数。
/// 握手看门狗仍用 WatchdogSeconds；UI 剩余秒数应使用本值。
/// </summary>
public static class ProcessDuration
{
    /// <summary>
    /// PLC 时长槽能表达的下限（秒）。0.2 是这一版 PLC 程序与仿真站共同的分辨率
    /// （<c>SimulatedPlcStation</c> 按 &gt;0.4 判定"有时长"），写得更小等于没写。
    /// </summary>
    public const double MinWritableSeconds = 0.2;

    /// <summary>
    /// PLC 时长槽能表达的上限（秒）= 单个工步 2 小时。这是<strong>设备</strong>的限制，不是产品的工艺限制：
    /// 要跑 24 小时固化得分解成多条工步，或者改 PLC 程序的时长语义。
    /// 以前这里是 <c>Math.Clamp</c>：超上限会被静默截成 7200 写给 PLC，而上位机仍按原始值等 ——
    /// 设定值被人改了，履历上却看不出来。现在一律报错。
    /// </summary>
    public const double MaxWritableSeconds = 7200;

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

    /// <summary>
    /// 单位换算。历史上只认 s/min/h，所以"2 d"这种时长既不会被换算、也不会被当成时长参数
    /// （<see cref="ParameterSemantics.IsDuration"/> 同一批 token），换工艺写天/毫秒就静默失真。
    /// </summary>
    public static TimeSpan ToTimeSpan(double value, string unit)
    {
        var u = (unit ?? string.Empty).Trim();
        if (u.Equals("ms", StringComparison.OrdinalIgnoreCase) || u.Contains("毫秒", StringComparison.Ordinal))
            return TimeSpan.FromMilliseconds(Math.Max(0, value));
        if (u.Equals("min", StringComparison.OrdinalIgnoreCase) || u.Equals("分钟", StringComparison.Ordinal))
            return TimeSpan.FromMinutes(Math.Max(0, value));
        if (u.Equals("h", StringComparison.OrdinalIgnoreCase) || u.Equals("hr", StringComparison.OrdinalIgnoreCase)
            || u.Equals("小时", StringComparison.Ordinal))
            return TimeSpan.FromHours(Math.Max(0, value));
        if (u.Equals("d", StringComparison.OrdinalIgnoreCase) || u.Equals("day", StringComparison.OrdinalIgnoreCase)
            || u.Equals("days", StringComparison.OrdinalIgnoreCase) || u.Equals("天", StringComparison.Ordinal))
            return TimeSpan.FromDays(Math.Max(0, value));
        return TimeSpan.FromSeconds(Math.Max(0, value));
    }

    /// <summary>
    /// 时长能不能写进 PLC 的时长槽。<c>0</c> 表示"这条工步没有时长语义"，放行；
    /// 其余必须落在 <see cref="MinWritableSeconds" />–<see cref="MaxWritableSeconds" /> 之间。
    /// </summary>
    public static bool IsWritable(TimeSpan duration)
    {
        var seconds = duration.TotalSeconds;
        return seconds <= 0 || seconds >= MinWritableSeconds && seconds <= MaxWritableSeconds;
    }

    /// <summary>超出可写窗口就抛。<c>context</c> 负责把"哪条工步、哪个参数"说清楚。</summary>
    public static void DemandWritable(string context, TimeSpan duration)
    {
        if (IsWritable(duration)) return;
        var seconds = duration.TotalSeconds;
        var why = seconds > MaxWritableSeconds
            ? $"超过单工步可写的 {MaxWritableSeconds / 3600:0.#} 小时上限"
            : $"短于时长槽可表达的 {MinWritableSeconds} 秒下限";
        throw new DomainException("DURATION_RANGE",
            $"{context}的时长 {Format(duration)} {why}（可写窗口 {MinWritableSeconds} 秒 – {MaxWritableSeconds / 3600:0.#} 小时）。" +
            "系统不会把它截断后写给 PLC：设定值被悄悄改掉，履历上是什么都看不出来。");
    }

    /// <summary>给人看的时长：1.5 小时而不是 5400 秒，报错时不用再心算。</summary>
    public static string Format(TimeSpan duration)
    {
        if (duration.TotalSeconds <= 0) return "0";
        if (duration.TotalMilliseconds < 1) return $"{duration.TotalMilliseconds:0.##} ms";
        if (duration.TotalDays >= 1) return $"{duration.TotalDays:0.##} d";
        if (duration.TotalHours >= 1) return $"{duration.TotalHours:0.##} h";
        if (duration.TotalMinutes >= 1) return $"{duration.TotalMinutes:0.##} min";
        return $"{duration.TotalSeconds:0.###} s";
    }

    /// <summary>
    /// 提交审核时逐参数检查时长能不能写给 PLC。
    /// 只查 <see cref="ToTimeSpan"/> 那条合成路径是不够的：显式放在时长槽（最后一槽）上的参数
    /// 是按原值直接写 PLC 的，从来不经过那条路。
    /// </summary>
    public static void DemandWritableProcedure(IEnumerable<RecipeStep> steps)
    {
        foreach (var step in steps)
            foreach (var parameter in step.Parameters.OrderBy(p => p.SlotIndex))
            {
                if (parameter.Setpoint <= 0) continue;
                if (ParameterSemantics.Resolve(parameter.Semantic, parameter.Name, parameter.EngineeringUnit)
                    != ParameterSemantic.Duration)
                    continue;
                DemandWritable(
                    $"工步 {step.Code}（{step.Name}）的参数「{parameter.Name}」",
                    ToTimeSpan(parameter.Setpoint, parameter.EngineeringUnit));
            }
    }
}
