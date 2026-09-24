using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;

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
    /// <summary>覆盖写入 PLC 的 Step_Type。空则回退为 <see cref="Type"/> 的整型。</summary>
    public int? PlcProgramId { get; private set; }
    /// <summary>该单元规程声明的设备类编码。空则设计器按程序号推断，开批不按类优先。</summary>
    public string? EquipmentClassCode { get; private set; }
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
        string? operation = null,
        int? plcProgramId = null,
        string? equipmentClassCode = null)
    {
        if (id is { } assigned && assigned != Guid.Empty)
            Id = assigned;
        RecipeVersionId = recipeVersionId;
        Code = code.Trim();
        Name = name.Trim();
        PlcProgram.EnsureValid(type, plcProgramId);
        Type = type;
        PlcProgramId = plcProgramId;
        Ordinal = ordinal;
        CanvasX = canvasX;
        CanvasY = canvasY;
        WatchdogSeconds = watchdogSeconds <= 0 ? 120 : watchdogSeconds;
        Description = description;
        if (unitProcedure is not null && string.IsNullOrWhiteSpace(unitProcedure))
            throw new DomainException("ISA88_UNIT", $"工步 {Code} 必须填写单元规程（Unit Procedure）。");
        if (operation is not null && string.IsNullOrWhiteSpace(operation))
            throw new DomainException("ISA88_OP", $"工步 {Code} 必须填写操作（Operation）。");
        UnitProcedure = string.IsNullOrWhiteSpace(unitProcedure) ? Isa88.DefaultUnitProcedure : unitProcedure.Trim();
        Operation = string.IsNullOrWhiteSpace(operation) ? Isa88.DefaultOperation(type) : operation.Trim();
        EquipmentClassCode = RecipeUnitClass.Normalize(equipmentClassCode);
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
            operation: Operation,
            plcProgramId: PlcProgramId,
            equipmentClassCode: EquipmentClassCode);
        foreach (var parameter in clone.Parameters)
            parameter.AttachTo(clone.Id);
        return clone;
    }

    /// <summary>把已乱码的名称/说明换成权威副本的值；返回是否真的改到了。</summary>
    internal bool RestoreIfMojibake(string name, string? description)
    {
        var changed = false;
        if (IsMojibake(Name) && Name != name)
        {
            Name = name;
            changed = true;
        }
        if (description is not null && IsMojibake(Description ?? string.Empty) && Description != description)
        {
            Description = description;
            changed = true;
        }
        return changed;
    }

    /// <summary>补齐 ISA-88 单元/操作名；返回是否写了值。</summary>
    public bool EnsureIsa88()
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(UnitProcedure))
        {
            UnitProcedure = Isa88.DefaultUnitProcedure;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(Operation))
        {
            Operation = Isa88.DefaultOperation(Type);
            changed = true;
        }
        return changed;
    }

    internal bool AssignEquipmentClass(string? code)
    {
        var normalized = RecipeUnitClass.Normalize(code);
        if (normalized == EquipmentClassCode)
            return false;
        EquipmentClassCode = normalized;
        Touch();
        return true;
    }

    internal bool AddParameter(RecipeParameter parameter)
    {
        if (Parameters.Any(p => p.SlotIndex == parameter.SlotIndex))
            return false;
        parameter.AttachTo(Id);
        Parameters.Add(parameter);
        return true;
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
    /// <summary>显式语义。Unspecified 时按名称/单位推断（历史数据回退）。</summary>
    public ParameterSemantic Semantic { get; private set; }
    /// <summary>归档时读取的实测点键名，对应点表 Measured 的 key。空则按语义推断。</summary>
    public string? MeasuredTag { get; private set; }

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
        bool scaleWithBatch = false,
        ParameterSemantic semantic = ParameterSemantic.Unspecified,
        string? measuredTag = null)
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
        Semantic = semantic;
        MeasuredTag = string.IsNullOrWhiteSpace(measuredTag) ? null : measuredTag.Trim();
    }

    internal RecipeParameter Clone() =>
        new(SlotIndex, Name, EngineeringUnit, Setpoint, Min, Max, WriteToPlc, ArchiveAsQuality, ScaleWithBatch, Semantic, MeasuredTag);

    internal void AttachTo(Guid recipeStepId) => RecipeStepId = recipeStepId;

    internal bool RestoreIfMojibake(string name, string engineeringUnit)
    {
        var changed = false;
        if (RecipeStep.IsMojibake(Name) && Name != name)
        {
            Name = name;
            changed = true;
        }
        if (RecipeStep.IsMojibake(EngineeringUnit) && EngineeringUnit != engineeringUnit)
        {
            EngineeringUnit = engineeringUnit;
            changed = true;
        }
        return RestoreRateUnitIfCorrupted() || changed;
    }

    internal bool RestoreRateUnitIfCorrupted()
    {
        if (!EngineeringUnit.Contains('?'))
            return false;
        if (!Name.Contains("斜率", StringComparison.Ordinal) && !EngineeringUnit.Contains("/min", StringComparison.OrdinalIgnoreCase))
            return false;
        EngineeringUnit = "℃/min";
        return true;
    }
}

/// <summary>
/// 版本审批记录 = 链上一个节点在提交那一刻的**冻结副本**。
/// 名称、所需角色、两种签名含义都是自己的列，不是指向链配置的外键：
/// 管理员日后怎么改链，都不能追溯性改变别人已经签下或即将签下的含义（与控制配方快照同一套纪律）。
/// </summary>
public sealed class ApprovalRecord : Entity
{
    public Guid RecipeVersionId { get; private set; }
    /// <summary>链上顺序，1 起；0 是提交动作。推进与"还差谁签"只认这个顺序，不认名字。</summary>
    public int Seq { get; private set; }
    /// <summary>稳定标识，仅用于历史行回填与缺省文案，不参与授权判定。</summary>
    public ApprovalNode Node { get; private set; }
    public string Title { get; private set; } = string.Empty;
    /// <summary>这个节点要求谁签。一条链内不重复由 <see cref="ApprovalChain.Validate"/> 保证。</summary>
    public UserRole RequiredRole { get; private set; }
    public string MeaningApproved { get; private set; } = string.Empty;
    public string MeaningRejected { get; private set; } = string.Empty;
    public ApprovalDecision Decision { get; private set; } = ApprovalDecision.Pending;
    public Guid? ReviewerId { get; private set; }
    public string? ReviewerName { get; private set; }
    public string? Comment { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>
    /// 展示与审计用的签名含义：已决 = 他签下的那句，未决 = 待签提示。
    /// 派生自同一行上的冻结副本，所以它永远等于签署人当时看到的那句话。
    /// 只读、不落库（AppDbContext 里显式 Ignore）。
    /// </summary>
    public string Meaning => Decision switch
    {
        ApprovalDecision.Approved => MeaningApproved,
        ApprovalDecision.Rejected => MeaningRejected,
        _ => ApprovalChainStep.Prompt(Title)
    };

    private ApprovalRecord() { }

    private ApprovalRecord(Guid versionId, int seq, ApprovalChainStep step)
    {
        RecipeVersionId = versionId;
        Seq = seq;
        Node = step.Node;
        Title = step.Title;
        RequiredRole = step.RequiredRole;
        MeaningApproved = step.MeaningApproved;
        MeaningRejected = step.MeaningRejected;
    }

    public static ApprovalRecord Pending(Guid versionId, int seq, ApprovalChainStep step) => new(versionId, seq, step);

    /// <summary>迁移回填历史行与测试构造用：按节点标识取缺省名称与两种含义。</summary>
    public static ApprovalRecord Open(Guid versionId, ApprovalNode node, int seq) =>
        Pending(versionId, seq, new ApprovalChainStep(
            node, node.ToTitle(), node.ToRequiredRole(),
            node.ToDefaultMeaning(ApprovalDecision.Approved),
            node.ToDefaultMeaning(ApprovalDecision.Rejected)));

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
