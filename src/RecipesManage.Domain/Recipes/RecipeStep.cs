using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Recipes;

public sealed class RecipeStep : Entity
{
    public Guid RecipeVersionId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public StepType Type { get; private set; }
    public int Ordinal { get; private set; }
    public double CanvasX { get; private set; }
    public double CanvasY { get; private set; }
    public int WatchdogSeconds { get; private set; } = 120;
    public string? Description { get; private set; }
    /// <summary>ISA-88 Unit Procedure 名称（同一设备单元内的工步组）。</summary>
    public string? UnitProcedure { get; private set; }
    /// <summary>ISA-88 Operation 名称；本系统工步对应 Phase。</summary>
    public string? Operation { get; private set; }
    public List<RecipeParameter> Parameters { get; private set; } = [];

    private RecipeStep() { }

    public RecipeStep(
        Guid recipeVersionId,
        string code,
        string name,
        StepType type,
        int ordinal,
        double canvasX,
        double canvasY,
        int watchdogSeconds,
        string? description,
        IEnumerable<RecipeParameter> parameters,
        Guid? id = null,
        string? unitProcedure = null,
        string? operation = null)
    {
        if (id is { } assigned && assigned != Guid.Empty)
            Id = assigned;
        RecipeVersionId = recipeVersionId;
        Code = code.Trim();
        Name = name.Trim();
        Type = type;
        Ordinal = ordinal;
        CanvasX = canvasX;
        CanvasY = canvasY;
        WatchdogSeconds = watchdogSeconds <= 0 ? 120 : watchdogSeconds;
        Description = description;
        UnitProcedure = string.IsNullOrWhiteSpace(unitProcedure) ? Isa88.DefaultUnitProcedure : unitProcedure.Trim();
        Operation = string.IsNullOrWhiteSpace(operation) ? Isa88.DefaultOperation(type) : operation.Trim();
        Parameters = parameters.ToList();
    }

    internal RecipeStep CloneTo(Guid newVersionId)
    {
        var clone = new RecipeStep(
            newVersionId,
            Code,
            Name,
            Type,
            Ordinal,
            CanvasX,
            CanvasY,
            WatchdogSeconds,
            Description,
            Parameters.Select(p => p.Clone()),
            unitProcedure: UnitProcedure,
            operation: Operation);
        foreach (var parameter in clone.Parameters)
            parameter.AttachTo(clone.Id);
        return clone;
    }

    internal void RestoreIfMojibake(string name, string? description)
    {
        if (IsMojibake(Name))
            Name = name;
        if (description is not null && IsMojibake(Description ?? string.Empty))
            Description = description;
    }

    public void EnsureIsa88()
    {
        if (string.IsNullOrWhiteSpace(UnitProcedure))
            UnitProcedure = Isa88.DefaultUnitProcedure;
        if (string.IsNullOrWhiteSpace(Operation))
            Operation = Isa88.DefaultOperation(Type);
    }

    internal void AddParameter(RecipeParameter parameter)
    {
        if (Parameters.Any(p => p.SlotIndex == parameter.SlotIndex))
            return;
        parameter.AttachTo(Id);
        Parameters.Add(parameter);
    }

    internal static bool IsMojibake(string value) =>
        value.Length > 0 && value.Contains('?');
}

public sealed class RecipeEdge : Entity
{
    public Guid RecipeVersionId { get; private set; }
    public Guid FromStepId { get; private set; }
    public Guid ToStepId { get; private set; }

    private RecipeEdge() { }

    public RecipeEdge(Guid recipeVersionId, Guid fromStepId, Guid toStepId)
    {
        RecipeVersionId = recipeVersionId;
        FromStepId = fromStepId;
        ToStepId = toStepId;
    }
}

public sealed class RecipeParameter : Entity
{
    public const int MaxSlots = 16;

    public Guid RecipeStepId { get; private set; }
    public int SlotIndex { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string EngineeringUnit { get; private set; } = string.Empty;
    public double Setpoint { get; private set; }
    public double? Min { get; private set; }
    public double? Max { get; private set; }
    public bool WriteToPlc { get; private set; } = true;
    public bool ArchiveAsQuality { get; private set; }
    /// <summary>为 true 时，创建批次的缩放因子会乘到设定值与上下限（装炉量等）；温度/时间等保持 false。</summary>
    public bool ScaleWithBatch { get; private set; }

    private RecipeParameter() { }

    public RecipeParameter(
        int slotIndex,
        string name,
        string engineeringUnit,
        double setpoint,
        double? min,
        double? max,
        bool writeToPlc,
        bool archiveAsQuality,
        bool scaleWithBatch = false)
    {
        if (slotIndex is < 0 or >= MaxSlots)
            throw new DomainException("PARAM_SLOT", $"参数槽位必须在 0..{MaxSlots - 1}。");
        SlotIndex = slotIndex;
        Name = name.Trim();
        EngineeringUnit = engineeringUnit.Trim();
        Setpoint = setpoint;
        Min = min;
        Max = max;
        WriteToPlc = writeToPlc;
        ArchiveAsQuality = archiveAsQuality;
        ScaleWithBatch = scaleWithBatch;
    }

    internal RecipeParameter Clone() =>
        new(SlotIndex, Name, EngineeringUnit, Setpoint, Min, Max, WriteToPlc, ArchiveAsQuality, ScaleWithBatch);

    internal void AttachTo(Guid recipeStepId) => RecipeStepId = recipeStepId;

    internal void RestoreIfMojibake(string name, string engineeringUnit)
    {
        if (RecipeStep.IsMojibake(Name))
            Name = name;
        if (RecipeStep.IsMojibake(EngineeringUnit))
            EngineeringUnit = engineeringUnit;
        RestoreRateUnitIfCorrupted();
    }

    internal void RestoreRateUnitIfCorrupted()
    {
        if (!EngineeringUnit.Contains('?'))
            return;
        if (Name.Contains("斜率", StringComparison.Ordinal) || EngineeringUnit.Contains("/min", StringComparison.OrdinalIgnoreCase))
            EngineeringUnit = "℃/min";
    }
}

public sealed class ApprovalRecord : Entity
{
    public Guid RecipeVersionId { get; private set; }
    public ApprovalLevel Level { get; private set; }
    public ApprovalDecision Decision { get; private set; } = ApprovalDecision.Pending;
    public Guid? ReviewerId { get; private set; }
    public string? ReviewerName { get; private set; }
    public string? Comment { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    private ApprovalRecord() { }

    public static ApprovalRecord Open(Guid versionId, ApprovalLevel level) =>
        new() { RecipeVersionId = versionId, Level = level };

    public void Complete(Guid reviewerId, string reviewerName, ApprovalDecision decision, string? comment, DateTimeOffset now)
    {
        if (Decision != ApprovalDecision.Pending)
            throw new DomainException("ALREADY_DECIDED", "该审核节点已处理。");
        if (decision == ApprovalDecision.Pending)
            throw new DomainException("INVALID_DECISION", "审核结论无效。");

        ReviewerId = reviewerId;
        ReviewerName = reviewerName;
        Decision = decision;
        Comment = comment;
        DecidedAt = now;
        Touch();
    }
}
