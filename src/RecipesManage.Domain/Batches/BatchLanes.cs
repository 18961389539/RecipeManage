using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Domain.Batches;

/// <summary>
/// ISA-88 并行 Unit Procedure 时，每个绑定 PLC 一条握手车道。
/// 同一设备仍串行；不同设备可同时处于 PLC_Ready / Step_Running。
/// </summary>
public sealed record BatchLaneState(
    string EquipmentCode,
    Guid EquipmentId,
    string UnitProcedure,
    Guid? StepId,
    string StepCode,
    string Phase,
    StepOutcome Outcome);

public static class BatchLanes
{
    /// <summary>
    /// 批次列 <c>HandshakePhase</c> 的唯一生产者：把车道行汇总成展示串。
    /// 单车道只写相位本身（未开批、单设备批次的历史读法都依赖这个形状），多车道按设备码序拼
    /// <c>CODE:Phase</c>。以前这里还有一份"读—改—写"式的 <c>Merge(current, code, phase)</c>，
    /// 与调度器各自拼一次串；那条路径已无人调用，删掉，避免两处格式漂移。
    /// 分隔符取自 <see cref="HandshakeView.Separator"/>：串只有一份格式定义。
    /// </summary>
    public static string Format(IReadOnlyList<BatchLane> lanes, string fallback) => lanes.Count switch
    {
        0 => fallback,
        1 => lanes[0].Phase,
        _ => string.Join(HandshakeView.Separator, lanes
            .OrderBy(l => l.EquipmentCode, StringComparer.Ordinal)
            .Select(l => $"{l.EquipmentCode}:{l.Phase}"))
    };

    /// <summary>
    /// 只为"从来没有车道行"的历史批次重建展示。车道<b>当前</b>相位的真源是 batch_lanes 行
    /// （见 <see cref="FromRows"/>）；这里不解析批次展示串——那是 <see cref="Format"/> 的输出，
    /// 读回来就成了第二个真源。重建顺序：该工步最后一条握手事件 → 工步结论映射。
    /// </summary>
    public static IReadOnlyList<BatchLaneState> Build(
        ControlRecipeSnapshot snapshot,
        Guid primaryEquipmentId,
        IReadOnlyList<BatchStepExecution> executions,
        IReadOnlyList<HandshakeEvent> events,
        IReadOnlyDictionary<Guid, string> equipmentCodes)
    {
        var execByStep = executions.ToDictionary(e => e.StepId);
        var lastPhaseByStep = events
            .Where(e => e.StepId is not null)
            .GroupBy(e => e.StepId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(e => e.CreatedAt).First().Phase);

        return snapshot.Steps
            .Select(step => (
                Step: step,
                EquipmentId: UnitEquipmentBinding.Resolve(snapshot, step.UnitProcedure, primaryEquipmentId)))
            .GroupBy(x => x.EquipmentId)
            .OrderBy(g => equipmentCodes.GetValueOrDefault(g.Key, g.Key.ToString("N")), StringComparer.Ordinal)
            .Select(group => ToLane(
                group.Key,
                group.Select(x => x.Step).ToList(),
                execByStep,
                lastPhaseByStep,
                equipmentCodes))
            .ToList();
    }

    public static IReadOnlyList<BatchLaneState> FromRows(IEnumerable<BatchLane> rows) =>
        rows
            .OrderBy(r => r.EquipmentCode, StringComparer.Ordinal)
            .Select(r => new BatchLaneState(
                r.EquipmentCode, r.EquipmentId, r.UnitProcedure, r.StepId, r.StepCode, r.Phase, r.Outcome))
            .ToList();

    public static bool IsSkipSafePhase(string? phase) => HandshakeView.IsSkipSafe(phase);

    private static BatchLaneState ToLane(
        Guid equipmentId,
        IReadOnlyList<SnapshotStep> steps,
        IReadOnlyDictionary<Guid, BatchStepExecution> execByStep,
        IReadOnlyDictionary<Guid, string> lastPhaseByStep,
        IReadOnlyDictionary<Guid, string> equipmentCodes)
    {
        var code = equipmentCodes.GetValueOrDefault(equipmentId, equipmentId.ToString("N")[..8]);
        var running = steps
            .Select(s => execByStep.GetValueOrDefault(s.StepId))
            .FirstOrDefault(e => e is { Outcome: StepOutcome.Running or StepOutcome.Held or StepOutcome.AwaitingConfirm });
        var current = running
            ?? steps.Select(s => execByStep.GetValueOrDefault(s.StepId))
                .LastOrDefault(e => e is { Outcome: StepOutcome.Completed or StepOutcome.Skipped or StepOutcome.Faulted })
            ?? steps.Select(s => execByStep.GetValueOrDefault(s.StepId)).FirstOrDefault();
        var snap = current is null
            ? steps[0]
            : steps.First(s => s.StepId == current.StepId);
        var outcome = current?.Outcome ?? StepOutcome.Pending;

        // 事件里可能存着早年写进去的展示串（"HT-A:Held · HT-B:…"），只认相位令牌。
        var phase = current is not null
                    && lastPhaseByStep.TryGetValue(current.StepId, out var fromEvent)
                    && HandshakeView.IsHandshakeToken(fromEvent)
            ? HandshakeView.Token(fromEvent)
            : HandshakeView.FromStepOutcome(outcome);

        return new BatchLaneState(
            code,
            equipmentId,
            Isa88.UnitName(snap.UnitProcedure),
            current?.StepId ?? snap.StepId,
            snap.Code,
            phase,
            outcome);
    }
}
