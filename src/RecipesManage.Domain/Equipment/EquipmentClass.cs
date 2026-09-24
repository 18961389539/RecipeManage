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
        IReadOnlyList<PhaseParameterSpec> parameters,
        int? plcProgramId = null)
    {
        EnsureProcessPhase(stepType);
        EnsureProgramAvailable(stepType, plcProgramId, exceptId: null);
        var template = new PhaseTemplate(Id, code, name, stepType, operation, watchdogSeconds, parameters, plcProgramId);
        Templates.Add(template);
        return template;
    }

    public PhaseTemplate RequireTemplate(Guid id) =>
        Templates.FirstOrDefault(t => t.Id == id)
        ?? throw new DomainException("NOT_FOUND", "相模板不存在。");

    public void UpdateTemplate(
        Guid id,
        string name,
        StepType stepType,
        string? operation,
        int watchdogSeconds,
        IReadOnlyList<PhaseParameterSpec> parameters,
        int? plcProgramId)
    {
        var template = RequireTemplate(id);
        EnsureProcessPhase(stepType);
        EnsureProgramAvailable(stepType, plcProgramId, template.Id);
        template.Replace(name, stepType, operation, watchdogSeconds, parameters, plcProgramId);
    }

    public void RemoveTemplate(PhaseTemplate template)
    {
        Templates.Remove(template);
        Touch();
    }

    private void EnsureProgramAvailable(StepType stepType, int? plcProgramId, Guid? exceptId)
    {
        var programId = PlcProgram.Resolve(stepType, plcProgramId);
        if (Templates.Any(t => t.Id != exceptId && PlcProgram.Resolve(t.StepType, t.PlcProgramId) == programId))
            throw new DomainException("PLC_PROGRAM", $"该类已有程序号 {programId} 的相模板。");
    }

    private static void EnsureProcessPhase(StepType stepType)
    {
        if (PlcProgram.IsHost(stepType))
            throw new DomainException("PHASE_HOST", "设备类相模板必须是写 PLC 的工艺相，等待/质检/人工确认由上位机执行。");
    }

    public IReadOnlySet<StepType> AllowedProcessTypes() =>
        Templates.Select(t => t.StepType).Where(t => !PlcProgram.IsHost(t)).ToHashSet();

    public IReadOnlySet<int> AllowedProgramIds() =>
        Templates
            .Where(t => PlcProgram.WritesToPlc(t.StepType))
            .Select(t => PlcProgram.Resolve(t.StepType, t.PlcProgramId))
            .ToHashSet();
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
    /// <summary>覆盖写入 PLC 的 Step_Type。空则回退为 <see cref="StepType"/> 整型。</summary>
    public int? PlcProgramId { get; private set; }
    public string ParametersJson { get; private set; } = "[]";

    private PhaseTemplate() { }

    public PhaseTemplate(
        Guid equipmentClassId,
        string code,
        string name,
        StepType stepType,
        string? operation,
        int watchdogSeconds,
        IReadOnlyList<PhaseParameterSpec> parameters,
        int? plcProgramId = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new DomainException("PHASE_CODE", "相模板编码不能为空。");
        PlcProgram.EnsureValid(stepType, plcProgramId);
        if (PlcProgram.IsHost(stepType))
            throw new DomainException("PHASE_HOST", "设备类相模板必须是写 PLC 的工艺相，等待/质检/人工确认由上位机执行。");
        EquipmentClassId = equipmentClassId;
        Code = code.Trim().ToUpperInvariant();
        Name = string.IsNullOrWhiteSpace(name) ? stepType.ToString() : name.Trim();
        StepType = stepType;
        PlcProgramId = plcProgramId;
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

    public void Replace(
        string name,
        StepType stepType,
        string? operation,
        int watchdogSeconds,
        IReadOnlyList<PhaseParameterSpec> parameters,
        int? plcProgramId)
    {
        if (PlcProgram.IsHost(stepType))
            throw new DomainException("PHASE_HOST", "设备类相模板必须是写 PLC 的工艺相，等待/质检/人工确认由上位机执行。");
        PlcProgram.EnsureValid(stepType, plcProgramId);
        Name = string.IsNullOrWhiteSpace(name) ? stepType.ToString() : name.Trim();
        StepType = stepType;
        PlcProgramId = plcProgramId;
        Operation = string.IsNullOrWhiteSpace(operation) ? Isa88.DefaultOperation(stepType) : operation.Trim();
        WatchdogSeconds = watchdogSeconds <= 0 ? 120 : watchdogSeconds;
        ParametersJson = JsonSerializer.Serialize(parameters ?? [], Json);
        Touch();
    }

    internal bool AssignPlcProgramId(int? plcProgramId)
    {
        PlcProgram.EnsureValid(StepType, plcProgramId);
        if (PlcProgramId == plcProgramId)
            return false;
        PlcProgramId = plcProgramId;
        Touch();
        return true;
    }
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
    public ParameterSemantic Semantic { get; init; }
    public string? MeasuredTag { get; init; }

    public RecipeParameter ToParameter() =>
        new(SlotIndex, Name, EngineeringUnit, Setpoint, Min, Max, WriteToPlc, ArchiveAsQuality, ScaleWithBatch,
            Semantic, MeasuredTag);
}

public static class EquipmentClassRules
{
    public static bool IsHostPhase(StepType type) => PlcProgram.IsHost(type);

    public static void EnsureCompatible(
        IEnumerable<(string Code, StepType Type, int? PlcProgramId, string? UnitProcedure, string? EquipmentClassCode)> phases,
        Func<string?, Guid> resolveEquipment,
        IReadOnlyDictionary<Guid, string> equipmentCodes,
        IReadOnlyDictionary<Guid, string?> classByEquipment,
        IReadOnlyDictionary<string, IReadOnlySet<int>> allowedByClass)
    {
        foreach (var step in phases)
        {
            var equipmentId = resolveEquipment(step.UnitProcedure);
            classByEquipment.TryGetValue(equipmentId, out var classCode);
            equipmentCodes.TryGetValue(equipmentId, out var eqCode);
            if (RecipeUnitClass.Conflicts(step.EquipmentClassCode, classCode))
                throw new DomainException(
                    "EQ_CLASS",
                    $"设备 {eqCode ?? equipmentId.ToString()[..8]} 属于类 {classCode}，与单元声明的 {RecipeUnitClass.Normalize(step.EquipmentClassCode)} 不符（工步 {step.Code}）。");
            if (PlcProgram.IsHost(step.Type))
                continue;
            if (string.IsNullOrWhiteSpace(classCode))
                continue;
            var programId = PlcProgram.Resolve(step.Type, step.PlcProgramId);
            if (!allowedByClass.TryGetValue(classCode, out var allowed) || allowed.Contains(programId))
                continue;
            throw new DomainException(
                "EQ_CLASS",
                $"设备 {eqCode ?? equipmentId.ToString()[..8]} 属于类 {classCode}，不允许执行程序 {programId}（{step.Type}）相（工步 {step.Code}）。");
        }
    }
}
