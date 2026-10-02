using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Domain.Batches;

/// <summary>
/// 从冻结的控制配方快照生成与调度引擎相同的 PLC 写参计划。
/// Wait / ManualConfirm / QualityCheck 禁止写 PLC。
/// </summary>
public static class ControlRecipeWritePlan
{
    public static bool WritesToPlc(StepType type) => PlcProgram.WritesToPlc(type);

    /// <summary>写参帧里承载工艺时长的槽位。点表槽数不足时，它是最容易被静默丢掉的一个。</summary>
    public const int DurationSlot = RecipeParameter.MaxSlots - 1;

    public static float[] PackParameters(SnapshotStep step)
    {
        var parameters = new float[RecipeParameter.MaxSlots];
        if (!WritesToPlc(step.Type))
            return parameters;

        foreach (var parameter in step.Parameters.Where(p => p.WriteToPlc && (uint)p.SlotIndex < RecipeParameter.MaxSlots))
            parameters[parameter.SlotIndex] = (float)parameter.Setpoint;
        if (ProcessDuration.TryFrom(step) is { } processDuration && parameters[DurationSlot] == 0)
        {
            // 超窗口就抛。以前这里是 Math.Clamp：24 小时的固化会被写成 2 小时发给 PLC，
            // 而上位机按 24 小时等 —— 设定值被静默改掉，批记录与履历里什么都看不出来。
            ProcessDuration.DemandWritable($"工步 {step.Code}（{step.Name}）", processDuration);
            parameters[DurationSlot] = (float)processDuration.TotalSeconds;
        }
        return parameters;
    }

    /// <summary>
    /// 这一步真正会打进写参帧的槽位。
    /// 驱动只写点表里有的前 N 槽，所以点表短于这里任何一个槽位，
    /// 对应设定值就会静默留在 PLC 的上一步值上——开批前必须比对，不能靠事后看趋势发现。
    /// </summary>
    public static IReadOnlyList<int> WrittenSlots(SnapshotStep step)
    {
        if (!WritesToPlc(step.Type))
            return [];
        var slots = step.Parameters
            .Where(p => p.WriteToPlc && (uint)p.SlotIndex < RecipeParameter.MaxSlots)
            .Select(p => p.SlotIndex)
            .ToHashSet();
        // 时长要么落在显式参数槽上，要么由这里补进 DurationSlot，两种情况这一槽都会被写。
        if (ProcessDuration.TryFrom(step) is not null)
            slots.Add(DurationSlot);
        return slots.Order().ToList();
    }

    public static HandshakeWorkContext ToWorkContext(SnapshotStep step) =>
        new(
            PlcStepIdentity.FromCode(step.Code, step.Ordinal),
            PlcProgram.Resolve(step.Type, step.PlcProgramId),
            PackParameters(step),
            TimeSpan.FromSeconds(Math.Max(step.WatchdogSeconds, 5)),
            ProcessDuration.TryFrom(step));

    public static PlcWritePlanItem ForStep(SnapshotStep step)
    {
        var writes = WritesToPlc(step.Type);
        return new PlcWritePlanItem(
            step.StepId,
            step.Code,
            step.Name,
            step.Type,
            PlcStepIdentity.FromCode(step.Code, step.Ordinal),
            PlcProgram.Resolve(step.Type, step.PlcProgramId),
            PackParameters(step),
            writes,
            writes
                ? "PLC_Ready 后写参并回读，再置 Trigger_Write"
                : "上位机工步，禁止写 PLC");
    }

    public static IReadOnlyList<PlcWritePlanItem> FromSnapshot(ControlRecipeSnapshot snapshot) =>
        snapshot.Steps.OrderBy(s => s.Ordinal).Select(ForStep).ToList();
}

public sealed record PlcWritePlanItem(
    Guid StepId,
    string StepCode,
    string StepName,
    StepType StepType,
    int PlcStepId,
    int PlcStepType,
    IReadOnlyList<float> Parameters,
    bool WriteToPlc,
    string Policy);
