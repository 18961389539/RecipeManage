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
    string Outcome);

public static class BatchLanes
{
    public const string Separator = " · ";

    public static string Merge(string? current, string equipmentCode, string phase, bool multiLane)
    {
        if (!multiLane || string.IsNullOrWhiteSpace(equipmentCode))
            return phase;
        var map = Parse(current);
        map[equipmentCode.Trim()] = phase;
        return string.Join(Separator, map
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}:{kv.Value}"));
    }

    public static Dictionary<string, string> Parse(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
            return map;

        var parts = text.Contains(Separator, StringComparison.Ordinal)
            ? text.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [text.Trim()];

        foreach (var part in parts)
        {
            var index = part.IndexOf(':');
            if (index <= 0 || index >= part.Length - 1)
                continue;
            var key = part[..index];
            if (key.Contains(' ', StringComparison.Ordinal))
                continue;
            map[key] = part[(index + 1)..];
        }

        return map;
    }

    public static IReadOnlyList<BatchLaneState> Build(
        ControlRecipeSnapshot snapshot,
        Guid primaryEquipmentId,
        IReadOnlyList<BatchStepExecution> executions,
        IReadOnlyList<HandshakeEvent> events,
        IReadOnlyDictionary<Guid, string> equipmentCodes,
        string handshakePhase)
    {
        var execByStep = executions.ToDictionary(e => e.StepId);
        var phaseByEquipment = Parse(handshakePhase);
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
                phaseByEquipment,
                equipmentCodes,
                handshakePhase))
            .ToList();
    }

    public static bool IsSkipSafePhase(string? phase) =>
        phase is nameof(HandshakePhase.WaitingPlcReady) or "Held" or "AwaitingConfirm" or "HostWait";

    private static BatchLaneState ToLane(
        Guid equipmentId,
        IReadOnlyList<SnapshotStep> steps,
        IReadOnlyDictionary<Guid, BatchStepExecution> execByStep,
        IReadOnlyDictionary<Guid, string> lastPhaseByStep,
        IReadOnlyDictionary<string, string> phaseByEquipment,
        IReadOnlyDictionary<Guid, string> equipmentCodes,
        string handshakePhase)
    {
        var code = equipmentCodes.GetValueOrDefault(equipmentId, equipmentId.ToString("N")[..8]);
        var running = steps
            .Select(s => execByStep.GetValueOrDefault(s.StepId))
            .FirstOrDefault(e => e is { Outcome: "Running" or "Held" or "AwaitingConfirm" });
        var current = running
            ?? steps.Select(s => execByStep.GetValueOrDefault(s.StepId))
                .LastOrDefault(e => e is { Outcome: "Completed" or "Skipped" or "Faulted" })
            ?? steps.Select(s => execByStep.GetValueOrDefault(s.StepId)).FirstOrDefault();
        var snap = current is null
            ? steps[0]
            : steps.First(s => s.StepId == current.StepId);
        var outcome = current?.Outcome ?? "Pending";

        string phase;
        if (phaseByEquipment.TryGetValue(code, out var fromText))
            phase = fromText;
        else if (current is not null && lastPhaseByStep.TryGetValue(current.StepId, out var fromEvent))
            phase = fromEvent;
        else if (phaseByEquipment.Count == 0)
            phase = handshakePhase;
        else
            phase = outcome == "Running" ? nameof(HandshakePhase.WaitingPlcReady) : outcome;

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
