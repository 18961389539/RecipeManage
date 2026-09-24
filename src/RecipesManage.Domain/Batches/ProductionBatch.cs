using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;
using System.Text.Json.Serialization;

namespace RecipesManage.Domain.Batches;

public enum BatchStatus
{
    Created = 0,
    Queued = 1,
    Running = 2,
    Completed = 3,
    Faulted = 4,
    Aborted = 5,
    Held = 6,
    Released = 7,
    DispositionRejected = 8
}

public sealed class ProductionBatch : Entity, IConcurrencyStamped
{
    public string BatchNo { get; private set; } = string.Empty;
    public Guid MasterRecipeId { get; private set; }
    public Guid RecipeVersionId { get; private set; }
    public Guid EquipmentId { get; private set; }
    public string ProductCode { get; private set; } = string.Empty;
    public string ProductName { get; private set; } = string.Empty;
    public BatchStatus Status { get; private set; } = BatchStatus.Created;
    public string ControlRecipeJson { get; private set; } = "{}";
    public Guid? CurrentStepId { get; private set; }
    public int CurrentStepIndex { get; private set; }
    public string HandshakePhase { get; private set; } = nameof(Handshake.HandshakePhase.WaitingPlcReady);
    public string? FaultCode { get; private set; }
    public string? FaultMessage { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
    public string? ReleasedBy { get; private set; }
    public string? ReleaseComment { get; private set; }
    public Guid ConcurrencyStamp { get; private set; } = Guid.NewGuid();

    public List<BatchStepExecution> StepExecutions { get; private set; } = [];
    public List<ProcessSample> Samples { get; private set; } = [];

    private ProductionBatch() { }

    public void RotateConcurrencyStamp() => ConcurrencyStamp = Guid.NewGuid();

    protected override void OnTouch() => RotateConcurrencyStamp();

    public static ProductionBatch Create(
        string batchNo,
        Guid equipmentId,
        ControlRecipeSnapshot snapshot,
        string snapshotJson,
        Guid createdBy)
    {
        if (snapshot.Steps.Count == 0)
            throw new DomainException("EMPTY_SNAPSHOT", "控制配方快照没有可执行工步。");

        var batch = new ProductionBatch
        {
            BatchNo = batchNo,
            MasterRecipeId = snapshot.MasterRecipeId,
            RecipeVersionId = snapshot.RecipeVersionId,
            EquipmentId = equipmentId,
            ProductCode = snapshot.ProductCode,
            ProductName = snapshot.ProductName,
            ControlRecipeJson = snapshotJson,
            CreatedBy = createdBy,
            Status = BatchStatus.Created,
            CurrentStepIndex = 0,
            CurrentStepId = snapshot.Steps[0].StepId
        };
        return batch;
    }

    /// <summary>
    /// 终态 = 工艺已收尾，或人已做过处置决定。引擎不得再改写这些批次：
    /// <see cref="Fault"/> / <see cref="Complete"/> 会覆盖 FaultMessage、CompletedAt 这类追溯字段。
    /// Faulted 不在其中——故障批次要被操作员重新排队（见 <see cref="Queue"/>）。
    /// 这是全站唯一的终态判定点，调度器与 API 都从这里取，不要再各写一份名单。
    /// </summary>
    public static bool IsTerminalState(BatchStatus status) =>
        status is BatchStatus.Completed
             or BatchStatus.Aborted
             or BatchStatus.Released
             or BatchStatus.DispositionRejected;

    public bool IsTerminal => IsTerminalState(Status);

    public void Queue()
    {
        if (Status is not BatchStatus.Created and not BatchStatus.Faulted and not BatchStatus.Held)
            throw new DomainException("CANNOT_QUEUE", $"批次状态 {Status} 不能进入调度队列。");
        Status = BatchStatus.Queued;
        FaultCode = null;
        FaultMessage = null;
        Touch();
    }

    public void Hold(string reason)
    {
        if (Status == BatchStatus.Held)
            return;
        if (Status is not BatchStatus.Running and not BatchStatus.Queued)
            throw new DomainException("CANNOT_HOLD", $"批次状态 {Status} 不能保持。");
        Status = BatchStatus.Held;
        FaultMessage = string.IsNullOrWhiteSpace(reason) ? "操作员保持" : reason.Trim();
        Touch();
    }

    public void Resume()
    {
        if (Status is not BatchStatus.Held)
            throw new DomainException("CANNOT_RESUME", "只有保持中的批次可以恢复。");
        Status = BatchStatus.Queued;
        FaultMessage = null;
        Touch();
    }

    public void MarkRunning(DateTimeOffset now)
    {
        if (Status is not BatchStatus.Queued and not BatchStatus.Running)
            throw new DomainException("CANNOT_RUN", $"批次状态 {Status} 不能启动执行。");
        Status = BatchStatus.Running;
        StartedAt ??= now;
        Touch();
    }

    public void UpdateHandshake(string phase)
    {
        HandshakePhase = phase;
        Touch();
    }

    public void AdvanceTo(Guid? nextStepId, int nextIndex)
    {
        CurrentStepId = nextStepId;
        CurrentStepIndex = nextIndex;
        Touch();
    }

    public void Complete(DateTimeOffset now)
    {
        if (IsTerminal)
            throw new DomainException("ALREADY_DONE", $"批次状态 {Status} 已是终态，不能再次收尾。");
        Status = BatchStatus.Completed;
        CompletedAt = now;
        CurrentStepId = null;
        HandshakePhase = Handshake.HandshakeView.SealCompleted(HandshakePhase);
        Touch();
    }

    public void Fault(string code, string message)
    {
        // 引擎异常走的是这条：批次已被人中止/放行/拒收时，写回 Faulted 会把中止原因和时间戳抹掉。
        // 调用方要么先查 IsTerminal 再决定，要么接住这个异常只记日志。
        if (IsTerminal)
            throw new DomainException("ALREADY_DONE", $"批次状态 {Status} 已是终态，引擎异常不再改写批次事实（{code}）。");
        Status = BatchStatus.Faulted;
        FaultCode = code;
        FaultMessage = message;
        HandshakePhase = nameof(Handshake.HandshakePhase.Faulted);
        Touch();
    }

    public void Abort(string reason)
    {
        if (IsTerminal)
            throw new DomainException("ALREADY_DONE", "已完成、已中止或已处置的批次不能再次中止。");
        Status = BatchStatus.Aborted;
        FaultMessage = reason;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void Release(string reviewerName, string comment, DateTimeOffset now)
    {
        if (Status != BatchStatus.Completed)
            throw new DomainException("CANNOT_RELEASE", $"批次状态 {Status} 不能质量放行。");
        Status = BatchStatus.Released;
        ReleasedAt = now;
        ReleasedBy = string.IsNullOrWhiteSpace(reviewerName) ? "quality" : reviewerName.Trim();
        ReleaseComment = string.IsNullOrWhiteSpace(comment) ? "质量放行" : comment.Trim();
        Touch();
    }

    public void RejectDisposition(string reviewerName, string comment, DateTimeOffset now)
    {
        if (Status != BatchStatus.Completed)
            throw new DomainException("CANNOT_REJECT_LOT", $"批次状态 {Status} 不能拒收。");
        if (string.IsNullOrWhiteSpace(comment))
            throw new DomainException("REJECT_REASON", "拒收必须填写对照质检与握手归档的意见。");
        Status = BatchStatus.DispositionRejected;
        ReleasedAt = now;
        ReleasedBy = string.IsNullOrWhiteSpace(reviewerName) ? "quality" : reviewerName.Trim();
        ReleaseComment = comment.Trim();
        Touch();
    }
}

/// <summary>
/// 工步执行结论。<strong>成员名就是库里的 TEXT 值</strong>（EF 按字符串存取），
/// 所以改名等于改历史数据口径，必须配数据修复而不是顺手改。
/// 批次终态判定走 <see cref="ProductionBatch.IsTerminalState"/>，这里只描述单个工步。
/// </summary>
public enum StepOutcome
{
    Pending,
    Running,
    Held,
    AwaitingConfirm,
    Completed,
    Skipped,
    Faulted
}

public sealed class BatchStepExecution : Entity, IConcurrencyStamped
{
    public Guid BatchId { get; private set; }
    public Guid StepId { get; private set; }
    public string StepCode { get; private set; } = string.Empty;
    public string StepName { get; private set; } = string.Empty;
    public StepType StepType { get; private set; }
    public int Ordinal { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public StepOutcome Outcome { get; private set; } = StepOutcome.Pending;
    public string? QualityJson { get; private set; }
    public Guid ConcurrencyStamp { get; private set; } = Guid.NewGuid();

    private BatchStepExecution() { }

    public void RotateConcurrencyStamp() => ConcurrencyStamp = Guid.NewGuid();

    protected override void OnTouch() => RotateConcurrencyStamp();

    public BatchStepExecution(Guid batchId, Guid stepId, string code, string name, StepType type, int ordinal)
    {
        BatchId = batchId;
        StepId = stepId;
        StepCode = code;
        StepName = name;
        StepType = type;
        Ordinal = ordinal;
    }

    public void MarkStarted(DateTimeOffset now)
    {
        StartedAt = now;
        Outcome = StepOutcome.Running;
        Touch();
    }

    public void MarkCompleted(DateTimeOffset now, string qualityJson)
    {
        CompletedAt = now;
        Outcome = StepOutcome.Completed;
        QualityJson = qualityJson;
        Touch();
    }

    public void MarkFaulted(string reason)
    {
        Outcome = StepOutcome.Faulted;
        QualityJson = reason;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void MarkSkipped(string reason)
    {
        Outcome = StepOutcome.Skipped;
        QualityJson = reason;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void MarkHeld()
    {
        Outcome = StepOutcome.Held;
        Touch();
    }

    public void MarkAwaitingConfirm()
    {
        Outcome = StepOutcome.AwaitingConfirm;
        Touch();
    }

    public void ResumeFromHold()
    {
        if (Outcome != StepOutcome.Held)
            return;
        Outcome = StepOutcome.Running;
        Touch();
    }

    public void RevertToPending()
    {
        if (Outcome is StepOutcome.Running or StepOutcome.Faulted or StepOutcome.Held or StepOutcome.AwaitingConfirm)
        {
            Outcome = StepOutcome.Pending;
            StartedAt = null;
            CompletedAt = null;
            QualityJson = null;
            Touch();
        }
    }
}

public sealed class ProcessSample : Entity
{
    public Guid BatchId { get; private set; }
    public Guid? StepId { get; private set; }
    public DateTimeOffset SampledAt { get; private set; }
    public string Tag { get; private set; } = string.Empty;
    public double Value { get; private set; }
    public string? Unit { get; private set; }

    private ProcessSample() { }

    public ProcessSample(Guid batchId, Guid? stepId, DateTimeOffset sampledAt, string tag, double value, string? unit)
    {
        BatchId = batchId;
        StepId = stepId;
        SampledAt = sampledAt;
        Tag = tag;
        Value = value;
        Unit = unit;
    }
}

public sealed class HandshakeEvent : Entity
{
    public Guid BatchId { get; private set; }
    public Guid? StepId { get; private set; }
    public string StepCode { get; private set; } = string.Empty;
    public string Phase { get; private set; } = string.Empty;
    public string Kind { get; private set; } = string.Empty;
    public string? Detail { get; private set; }
    public double? RemainingSeconds { get; private set; }

    private HandshakeEvent() { }

    public HandshakeEvent(
        Guid batchId,
        Guid? stepId,
        string stepCode,
        string phase,
        string kind,
        string? detail,
        double? remainingSeconds)
    {
        BatchId = batchId;
        StepId = stepId;
        StepCode = stepCode;
        Phase = phase;
        Kind = kind;
        Detail = detail;
        RemainingSeconds = remainingSeconds;
    }
}

public sealed class ProcessAlarm : Entity
{
    public Guid BatchId { get; private set; }
    public string BatchNo { get; private set; } = string.Empty;
    public Guid? StepId { get; private set; }
    public string StepCode { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public string Severity { get; private set; } = "Fault";
    public string Message { get; private set; } = string.Empty;
    public DateTimeOffset RaisedAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public string? AcknowledgedBy { get; private set; }

    private ProcessAlarm() { }

    public ProcessAlarm(
        Guid batchId,
        string batchNo,
        Guid? stepId,
        string stepCode,
        string code,
        string severity,
        string message,
        DateTimeOffset raisedAt)
    {
        BatchId = batchId;
        BatchNo = batchNo;
        StepId = stepId;
        StepCode = stepCode;
        Code = code;
        Severity = severity;
        Message = message;
        RaisedAt = raisedAt;
    }

    public void Acknowledge(string userName, DateTimeOffset now)
    {
        if (AcknowledgedAt is not null)
            return;
        AcknowledgedAt = now;
        AcknowledgedBy = userName;
        Touch();
    }
}

public sealed class ControlRecipeSnapshot
{
    public Guid MasterRecipeId { get; init; }
    public Guid RecipeVersionId { get; init; }
    public int VersionNumber { get; init; }
    public string RecipeCode { get; init; } = string.Empty;
    public string RecipeName { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public DateTimeOffset FrozenAt { get; init; }
    public string? IntegrityHash { get; set; }
    /// <summary>相对主配方基准的批次缩放；1 表示不缩放。纳入完整性哈希。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ScaleFactor { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LotNumber { get; init; }
    /// <summary>ISA-88 单元规程 → 设备。缺省表示全部使用批次主设备。纳入完整性哈希。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, Guid>? UnitEquipment { get; init; }
    public IReadOnlyList<SnapshotStep> Steps { get; init; } = [];
    public IReadOnlyList<SnapshotEdge> Edges { get; init; } = [];
}

public sealed class SnapshotStep
{
    public Guid StepId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public StepType Type { get; init; }
    public int Ordinal { get; init; }
    public int WatchdogSeconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PlcProgramId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnitProcedure { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EquipmentClassCode { get; init; }
    public IReadOnlyList<SnapshotParameter> Parameters { get; init; } = [];
}

public sealed class SnapshotParameter
{
    public int SlotIndex { get; init; }
    public string Name { get; init; } = string.Empty;
    public string EngineeringUnit { get; init; } = string.Empty;
    public double Setpoint { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public bool WriteToPlc { get; init; }
    public bool ArchiveAsQuality { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ScaleWithBatch { get; init; }
    /// <summary>追加字段一律放在末尾并保持"默认值不写"，否则已密封快照的完整性哈希会全部失配。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ParameterSemantic Semantic { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MeasuredTag { get; init; }
}

public sealed class SnapshotEdge
{
    public Guid FromStepId { get; init; }
    public Guid ToStepId { get; init; }
}
