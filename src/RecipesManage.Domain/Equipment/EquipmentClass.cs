using System.Text.Json;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Domain.Equipment;

/// <summary>
/// ISA-88 Equipment Module 类：约束该设备实例允许执行的 Phase 类型。
/// Wait / QualityCheck / ManualConfirm 由上位机执行，不占用设备相能力。
/// </summary>
public sealed class EquipmentClass : Entity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public List<PhaseTemplate> Templates { get; private set; } = [];

    private EquipmentClass() { }

    public EquipmentClass(string code, string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new DomainException("EQ_CLASS", "设备类编码不能为空。");
        Code = code.Trim().ToUpperInvariant();
        Name = string.IsNullOrWhiteSpace(name) ? Code : name.Trim();
        Description = description;
    }

    public PhaseTemplate AddTemplate(
        string code,
        string name,
        StepType stepType,
        string? operation,
        int watchdogSeconds,
        IReadOnlyList<PhaseParameterSpec> parameters)
    {
        var template = new PhaseTemplate(Id, code, name, stepType, operation, watchdogSeconds, parameters);
        Templates.Add(template);
        return template;
    }

    public IReadOnlySet<StepType> AllowedProcessTypes() =>
        Templates.Select(t => t.StepType).Where(t => !EquipmentClassRules.IsHostPhase(t)).ToHashSet();
}

public sealed class PhaseTemplate : Entity
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public Guid EquipmentClassId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public StepType StepType { get; private set; }
    public string Operation { get; private set; } = string.Empty;
    public int WatchdogSeconds { get; private set; } = 120;
    public string ParametersJson { get; private set; } = "[]";

    private PhaseTemplate() { }

    public PhaseTemplate(
        Guid equipmentClassId,
        string code,
        string name,
        StepType stepType,
        string? operation,
        int watchdogSeconds,
        IReadOnlyList<PhaseParameterSpec> parameters)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new DomainException("PHASE_CODE", "相模板编码不能为空。");
        EquipmentClassId = equipmentClassId;
        Code = code.Trim().ToUpperInvariant();
        Name = string.IsNullOrWhiteSpace(name) ? stepType.ToString() : name.Trim();
        StepType = stepType;
        Operation = string.IsNullOrWhiteSpace(operation) ? Isa88.DefaultOperation(stepType) : operation.Trim();
        WatchdogSeconds = watchdogSeconds <= 0 ? 120 : watchdogSeconds;
        ParametersJson = JsonSerializer.Serialize(parameters ?? [], Json);
    }

    public IReadOnlyList<PhaseParameterSpec> Parameters()
    {
        try
        {
            return JsonSerializer.Deserialize<List<PhaseParameterSpec>>(ParametersJson, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public IReadOnlyList<RecipeParameter> MaterializeParameters() =>
        Parameters().Select(p => p.ToParameter()).ToList();
}

public sealed class PhaseParameterSpec
{
    public int SlotIndex { get; init; }
    public string Name { get; init; } = string.Empty;
    public string EngineeringUnit { get; init; } = string.Empty;
    public double Setpoint { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public bool WriteToPlc { get; init; }
    public bool ArchiveAsQuality { get; init; }
    public bool ScaleWithBatch { get; init; }

    public RecipeParameter ToParameter() =>
        new(SlotIndex, Name, EngineeringUnit, Setpoint, Min, Max, WriteToPlc, ArchiveAsQuality, ScaleWithBatch);
}

public static class EquipmentClassRules
{
    public static bool IsHostPhase(StepType type) =>
        type is StepType.Wait or StepType.QualityCheck or StepType.ManualConfirm;

    public static void EnsureCompatible(
        IEnumerable<(string Code, StepType Type, string? UnitProcedure)> phases,
        Func<string?, Guid> resolveEquipment,
        IReadOnlyDictionary<Guid, string> equipmentCodes,
        IReadOnlyDictionary<Guid, string?> classByEquipment,
        IReadOnlyDictionary<string, IReadOnlySet<StepType>> allowedByClass)
    {
        foreach (var step in phases)
        {
            if (IsHostPhase(step.Type))
                continue;
            var equipmentId = resolveEquipment(step.UnitProcedure);
            if (!classByEquipment.TryGetValue(equipmentId, out var classCode) || string.IsNullOrWhiteSpace(classCode))
                continue;
            if (!allowedByClass.TryGetValue(classCode, out var allowed) || allowed.Contains(step.Type))
                continue;
            equipmentCodes.TryGetValue(equipmentId, out var eqCode);
            throw new DomainException(
                "EQ_CLASS",
                $"设备 {eqCode ?? equipmentId.ToString()[..8]} 属于类 {classCode}，不允许执行 {step.Type} 相（工步 {step.Code}）。");
        }
    }
}
