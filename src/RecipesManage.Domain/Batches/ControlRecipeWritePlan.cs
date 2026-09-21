using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Domain.Batches;

/// <summary>
/// 从冻结的控制配方快照生成与调度引擎相同的 PLC 写参计划。
/// Wait / ManualConfirm / QualityCheck 禁止写 PLC。
/// </summary>
public static class ControlRecipeWritePlan
{
    public static bool WritesToPlc(StepType type) =>
        type is not (StepType.Wait or StepType.ManualConfirm or StepType.QualityCheck);

    public static float[] PackParameters(SnapshotStep step)
    {
        var parameters = new float[16];
        if (!WritesToPlc(step.Type))
            return parameters;

        foreach (var parameter in step.Parameters.Where(p => p.WriteToPlc && (uint)p.SlotIndex < 16))
            parameters[parameter.SlotIndex] = (float)parameter.Setpoint;
        if (ProcessDuration.TryFrom(step) is { } processDuration && parameters[15] == 0)
            parameters[15] = (float)Math.Clamp(processDuration.TotalSeconds, 0.2, 7200);
        return parameters;
    }

    public static HandshakeWorkContext ToWorkContext(SnapshotStep step) =>
        new(
            PlcStepIdentity.FromCode(step.Code, step.Ordinal),
            (int)step.Type,
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
            (int)step.Type,
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
