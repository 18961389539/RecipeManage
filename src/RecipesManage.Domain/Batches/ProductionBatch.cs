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
        HandshakePhase = nameof(Handshake.HandshakePhase.WaitingPlcReady);
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

    public void UpdateLaneHandshake(string equipmentCode, string phase, bool multiLane)
    {
        HandshakePhase = BatchLanes.Merge(HandshakePhase, equipmentCode, phase, multiLane);
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
        Status = BatchStatus.Completed;
        CompletedAt = now;
        CurrentStepId = null;
        HandshakePhase = "Completed";
        Touch();
    }

    public void Fault(string code, string message)
    {
        Status = BatchStatus.Faulted;
        FaultCode = code;
        FaultMessage = message;
        HandshakePhase = nameof(Handshake.HandshakePhase.Faulted);
        Touch();
    }

    public void Abort(string reason)
    {
        if (Status is BatchStatus.Completed or BatchStatus.Released or BatchStatus.DispositionRejected)
            throw new DomainException("ALREADY_DONE", "已完成或已放行批次不能中止。");
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
        HandshakePhase = "Released";
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
        HandshakePhase = "DispositionRejected";
        Touch();
    }
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
    public string Outcome { get; private set; } = "Pending";
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
        Outcome = "Running";
        Touch();
    }

    public void MarkCompleted(DateTimeOffset now, string qualityJson)
    {
        CompletedAt = now;
        Outcome = "Completed";
        QualityJson = qualityJson;
        Touch();
    }

    public void MarkFaulted(string reason)
    {
        Outcome = "Faulted";
        QualityJson = reason;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void MarkSkipped(string reason)
    {
        Outcome = "Skipped";
        QualityJson = reason;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void MarkHeld()
    {
        Outcome = "Held";
        Touch();
    }

    public void MarkAwaitingConfirm()
    {
        Outcome = "AwaitingConfirm";
        Touch();
    }

    public void ResumeFromHold()
    {
        if (Outcome != "Held")
            return;
        Outcome = "Running";
        Touch();
    }

    public void RevertToPending()
    {
        if (Outcome is "Running" or "Faulted" or "Held" or "AwaitingConfirm")
        {
            Outcome = "Pending";
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
    /// <summary>相对主配方基准的批次缩放；1 表示不缩放。不纳入完整性哈希，以免破坏历史快照。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ScaleFactor { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LotNumber { get; init; }
    /// <summary>ISA-88 单元规程 → 设备。缺省表示全部使用批次主设备。不纳入完整性哈希。</summary>
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
    public string? UnitProcedure { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operation { get; init; }
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
}

public sealed class SnapshotEdge
{
    public Guid FromStepId { get; init; }
    public Guid ToStepId { get; init; }
}
