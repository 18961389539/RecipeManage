using RecipesManage.Domain.Batches;

namespace RecipesManage.Domain.Recipes;

/// <summary>
/// 上位机 Wait 工步禁止写 PLC。保持后必须继续剩余秒数，禁止整段重跑。
/// </summary>
public static class HostWaitClock
{
    public static TimeSpan Planned(SnapshotStep step) =>
        ProcessDuration.TryFrom(step) ?? TimeSpan.FromSeconds(1);

    public static TimeSpan Resolve(TimeSpan planned, bool resumeHeld, double? leftoverSeconds)
    {
        var floor = planned <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : planned;
        if (!resumeHeld)
            return floor;
        if (leftoverSeconds is null)
            return floor;
        if (leftoverSeconds.Value <= 0)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds(leftoverSeconds.Value);
    }

    /// <summary>
    /// 调度进程在 Wait 中被打断后，按上次 wait 事件的剩余秒数扣掉已过时间，禁止整段重跑。
    /// </summary>
    public static TimeSpan LeftoverAfterInterrupt(
        TimeSpan planned,
        DateTimeOffset waitStartedAt,
        double? recordedRemainingSeconds,
        DateTimeOffset now)
    {
        var recorded = recordedRemainingSeconds ?? planned.TotalSeconds;
        if (recorded <= 0)
            return TimeSpan.Zero;
        var elapsed = Math.Max(0, (now - waitStartedAt).TotalSeconds);
        return TimeSpan.FromSeconds(Math.Max(0, recorded - elapsed));
    }
}
