using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Recipes;

/// <summary>
/// 工步下发给 PLC 的程序号（点表 Step_Type）。
/// 执行类由 <see cref="Kind"/> 映射为四类语义；工艺相可以很多，程序号与枚举解耦。
/// 未指定时回退为 <c>(int)StepType</c>。0 / 7 / 8 保留给上位机工步，禁止作为写 PLC 程序号。
/// </summary>
public static class PlcProgram
{
    public const int Max = 99;

    public static ExecutionKind Kind(StepType type) => type switch
    {
        StepType.Wait => ExecutionKind.Wait,
        StepType.QualityCheck => ExecutionKind.QualityCheck,
        StepType.ManualConfirm => ExecutionKind.ManualConfirm,
        _ => ExecutionKind.WritePlc
    };

    public static bool IsHost(StepType type) => Kind(type) is not ExecutionKind.WritePlc;

    public static bool WritesToPlc(StepType type) => Kind(type) is ExecutionKind.WritePlc;

    public static int Resolve(StepType type, int? plcProgramId) =>
        plcProgramId ?? (int)type;

    public static void EnsureValid(StepType type, int? plcProgramId)
    {
        if (IsHost(type))
        {
            if (plcProgramId is int hostId && hostId != (int)type)
                throw new DomainException(
                    "PLC_PROGRAM",
                    $"上位机工步 {type} 禁止指定 PLC 程序号 {hostId}。");
            return;
        }

        if (plcProgramId is null)
            return;

        var id = plcProgramId.Value;
        if (id is 0 or 7 or 8)
            throw new DomainException(
                "PLC_PROGRAM",
                $"程序号 {id} 保留给等待/质检/人工确认，写 PLC 工步请使用 1–6 或 9–{Max}。");
        if (id < 1 || id > Max)
            throw new DomainException(
                "PLC_PROGRAM",
                $"PLC 程序号必须在 1–{Max}（工步类型 {type}）。");
    }
}
