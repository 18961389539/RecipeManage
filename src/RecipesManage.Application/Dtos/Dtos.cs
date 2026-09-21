using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Materials;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Application.Dtos;

public sealed record LoginRequest(string UserName, string Password);
public sealed record LoginResponse(string Token, UserDto User);
public sealed record UserDto(Guid Id, string UserName, string DisplayName, UserRole Role, bool IsActive = true);

public sealed record RecipeListItemDto(
    Guid Id,
    string Code,
    string Name,
    string ProductCode,
    string ProductName,
    RecipeLifecycle Lifecycle,
    int? ApprovedVersion,
    RecipeStatus? DraftStatus,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> UnitProcedures,
    ApprovalLevel? PendingLevel = null,
    string? PendingMeaning = null,
    int? ReviewVersion = null);

public sealed record RecipeDetailDto(
    Guid Id,
    string Code,
    string Name,
    string ProductCode,
    string ProductName,
    string? Description,
    RecipeVersionDto? Draft,
    RecipeVersionDto? Approved,
    IReadOnlyList<RecipeVersionDto> Versions);

public sealed record RecipeVersionDto(
    Guid Id,
    int VersionNumber,
    RecipeStatus Status,
    string? ChangeNote,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ApprovedAt,
    IReadOnlyList<StepDto> Steps,
    IReadOnlyList<EdgeDto> Edges,
    IReadOnlyList<ApprovalDto> Approvals);

public sealed record StepDto(
    Guid Id,
    string Code,
    string Name,
    StepType Type,
    int Ordinal,
    double CanvasX,
    double CanvasY,
    int WatchdogSeconds,
    string? Description,
    string? UnitProcedure,
    string? Operation,
    IReadOnlyList<ParameterDto> Parameters);

public sealed record ParameterDto(
    Guid Id,
    int SlotIndex,
    string Name,
    string EngineeringUnit,
    double Setpoint,
    double? Min,
    double? Max,
    bool WriteToPlc,
    bool ArchiveAsQuality,
    bool ScaleWithBatch);

public sealed record EdgeDto(Guid Id, Guid FromStepId, Guid ToStepId);

public sealed record ApprovalDto(
    Guid Id,
    ApprovalLevel Level,
    ApprovalDecision Decision,
    string? ReviewerName,
    string? Comment,
    DateTimeOffset? DecidedAt,
    string Meaning);

public sealed record CreateRecipeRequest(
    string Code,
    string Name,
    string ProductCode,
    string ProductName,
    string? Description);

public sealed record UpdateRecipeRequest(
    string Name,
    string ProductCode,
    string ProductName,
    string? Description);

public sealed record SaveProcedureRequest(
    IReadOnlyList<SaveStepRequest> Steps,
    IReadOnlyList<SaveEdgeRequest> Edges,
    string Password,
    string? ChangeReason = null);

public sealed record SaveStepRequest(
    Guid Id,
    string Code,
    string Name,
    StepType Type,
    int Ordinal,
    double CanvasX,
    double CanvasY,
    int WatchdogSeconds,
    string? Description,
    string? UnitProcedure,
    string? Operation,
    IReadOnlyList<SaveParameterRequest> Parameters);

public sealed record SaveParameterRequest(
    int SlotIndex,
    string Name,
    string EngineeringUnit,
    double Setpoint,
    double? Min,
    double? Max,
    bool WriteToPlc,
    bool ArchiveAsQuality,
    bool ScaleWithBatch = false);

public sealed record SaveEdgeRequest(Guid FromStepId, Guid ToStepId);
public sealed record SubmitRecipeRequest(string Password, string? Comment);
public sealed record DecideRequest(ApprovalDecision Decision, string? Comment, string Password);
public sealed record NewVersionRequest(string ChangeNote, string Password);
public sealed record RecipePackageDto(DateTimeOffset ExportedAt, string Database, IReadOnlyList<RecipeDetailDto> Recipes);
public sealed record RecipeImportResultDto(int Created, int Skipped, IReadOnlyList<string> Messages);
public sealed record CreateUserRequest(string UserName, string DisplayName, string Password, UserRole Role);
public sealed record UpdateUserRequest(string DisplayName, UserRole Role, bool IsActive, string? NewPassword);

public sealed record CreateBatchRequest(
    string BatchNo,
    Guid RecipeId,
    Guid EquipmentId,
    double ScaleFactor = 1,
    string? LotNumber = null,
    IReadOnlyDictionary<string, Guid>? UnitEquipment = null,
    IReadOnlyList<Guid>? ChargeLotIds = null);
public sealed record EsignActionRequest(string Password, string? Reason, Guid? StepId = null);
public sealed record ConnectionTestDto(bool Connected, bool PlcReady, double LatencyMs, string Protocol, string Message);
public sealed record BatchListItemDto(
    Guid Id,
    string BatchNo,
    string RecipeName,
    int RecipeVersion,
    string EquipmentCode,
    string ProductName,
    BatchStatus Status,
    string HandshakePhase,
    int CurrentStepIndex,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt);

public sealed record BatchDetailDto(
    Guid Id,
    string BatchNo,
    BatchStatus Status,
    string HandshakePhase,
    string? FaultCode,
    string? FaultMessage,
    Guid EquipmentId,
    string EquipmentName,
    ControlRecipeSnapshot Snapshot,
    Guid? CurrentStepId,
    int CurrentStepIndex,
    IReadOnlyList<StepExecutionDto> StepExecutions,
    IReadOnlyList<LaneHandshakeDto> Lanes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string SnapshotIntegrity,
    IReadOnlyList<PlcWritePlanDto> WritePlan,
    string? ReleasedBy = null,
    DateTimeOffset? ReleasedAt = null,
    string? ReleaseComment = null);

public sealed record PlcWritePlanDto(
    Guid StepId,
    string StepCode,
    string StepName,
    StepType StepType,
    int PlcStepId,
    int PlcStepType,
    IReadOnlyList<float> Parameters,
    bool WriteToPlc,
    string Policy);

public sealed record LaneHandshakeDto(
    string EquipmentCode,
    Guid EquipmentId,
    string UnitProcedure,
    Guid? StepId,
    string StepCode,
    string Phase,
    string Outcome);

public sealed record StepExecutionDto(
    Guid StepId,
    string StepCode,
    string StepName,
    StepType StepType,
    int Ordinal,
    string Outcome,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? QualityJson);

public sealed record SampleDto(DateTimeOffset SampledAt, string Tag, double Value, string? Unit, Guid? StepId);

public sealed record EquipmentDto(
    Guid Id,
    string Code,
    string Name,
    PlcProtocol Protocol,
    string Host,
    int Port,
    string PlcModel,
    int Rack,
    int Slot,
    bool Enabled,
    string TagMapJson,
    string? Description,
    string? WatchdogJson,
    string Occupancy = "Idle",
    string? OccupyingBatchNo = null,
    Guid? OccupyingBatchId = null,
    string? EquipmentClassCode = null);

public sealed record UpsertEquipmentRequest(
    string Code,
    string Name,
    PlcProtocol Protocol,
    string Host,
    int Port,
    string PlcModel,
    int Rack,
    int Slot,
    bool Enabled,
    string TagMapJson,
    string? Description,
    string? WatchdogJson,
    string? EquipmentClassCode = null);

public sealed record EquipmentOccupancyDto(
    Guid EquipmentId,
    string Code,
    string Name,
    string Protocol,
    bool Enabled,
    string Occupancy,
    Guid? BatchId,
    string? BatchNo,
    string? BatchStatus,
    string? HandshakePhase);

public sealed record DashboardDto(
    int RunningBatches,
    int QueuedBatches,
    int DraftRecipes,
    int PendingApprovals,
    int ApprovedRecipes,
    int FaultedBatches,
    int OpenAlarms,
    IReadOnlyList<BatchListItemDto> LiveBatches,
    IReadOnlyList<EquipmentOccupancyDto> EquipmentOccupancy,
    int PendingReleaseBatches = 0,
    int PendingLabSamples = 0);

public sealed record MaterialLotDto(
    Guid Id,
    string LotNumber,
    string MaterialCode,
    string MaterialName,
    Guid? ParentLotId,
    MaterialLotSource Source,
    MaterialLotStatus Status,
    double? Quantity,
    string? Uom,
    Guid? ProducedBatchId,
    DateTimeOffset CreatedAt);

public sealed record BatchMaterialUseDto(
    Guid Id,
    Guid BatchId,
    string? BatchNo,
    Guid MaterialLotId,
    string LotNumber,
    string MaterialCode,
    MaterialUseRole Role,
    double? Quantity,
    Guid? StepId);

public sealed record LabSampleDto(
    Guid Id,
    string SampleCode,
    Guid BatchId,
    Guid? MaterialLotId,
    string? LotNumber,
    Guid? ParentSampleId,
    Guid? StepId,
    LabSampleType SampleType,
    LabSampleDisposition Disposition,
    string? ResultsJson,
    string TakenBy,
    DateTimeOffset TakenAt,
    string? DispositionBy,
    DateTimeOffset? DisposedAt,
    string? Comment);

public sealed record LotGenealogyDto(
    MaterialLotDto Lot,
    IReadOnlyList<MaterialLotDto> Ancestors,
    IReadOnlyList<MaterialLotDto> Descendants,
    IReadOnlyList<BatchMaterialUseDto> Uses);

public sealed record CreateMaterialLotRequest(
    string LotNumber,
    string MaterialCode,
    string MaterialName,
    double? Quantity = null,
    string? Uom = null);

public sealed record SplitLotRequest(string ChildLotNumber, double? Quantity = null);

public sealed record CreateLabSampleRequest(
    string SampleCode,
    LabSampleType SampleType,
    Guid? MaterialLotId = null,
    Guid? ParentSampleId = null,
    Guid? StepId = null,
    string? ResultsJson = null);

public sealed record LabSampleDispositionRequest(
    string Password,
    LabSampleDisposition Disposition,
    string? Comment = null);

public sealed record AuditLogDto(
    Guid Id,
    string UserName,
    string Action,
    string EntityType,
    string EntityId,
    string? Detail,
    DateTimeOffset At);

/// <summary>审计分页结果。Total 是筛选后的全量条数，前端据此算页数。</summary>
public sealed record AuditLogPageDto(
    int Total,
    IReadOnlyList<AuditLogDto> Items);

public sealed record RecipeVersionDiffDto(
    int FromVersion,
    int ToVersion,
    IReadOnlyList<string> AddedSteps,
    IReadOnlyList<string> RemovedSteps,
    IReadOnlyList<RecipeFieldChangeDto> Changes,
    IReadOnlyList<string> ChangedStepCodes);

public sealed record RecipeFieldChangeDto(string Path, string? Before, string? After);

public sealed record TagMapCheckDto(string Result, string Message);

public sealed record InjectSimulatorFaultRequest(string Mode);

public sealed record PhaseParameterDto(
    int SlotIndex,
    string Name,
    string EngineeringUnit,
    double Setpoint,
    double? Min,
    double? Max,
    bool WriteToPlc,
    bool ArchiveAsQuality,
    bool ScaleWithBatch);

public sealed record PhaseTemplateDto(
    Guid Id,
    string Code,
    string Name,
    StepType StepType,
    string Operation,
    int WatchdogSeconds,
    string ClassCode,
    IReadOnlyList<PhaseParameterDto> Parameters);

public sealed record EquipmentClassDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    IReadOnlyList<PhaseTemplateDto> Templates);

public sealed record HandshakeLogDto(
    DateTimeOffset At,
    string StepCode,
    string Phase,
    string Kind,
    string? Detail,
    double? RemainingSeconds);

public sealed record SnapshotDriftDto(
    string StepCode,
    string Parameter,
    double FrozenSetpoint,
    double? MasterSetpoint,
    bool Drifted);

public sealed record BatchRecordDto(
    Guid BatchId,
    string BatchNo,
    BatchStatus Status,
    string SnapshotIntegrity,
    ControlRecipeSnapshot Snapshot,
    IReadOnlyList<StepExecutionDto> StepExecutions,
    IReadOnlyList<HandshakeLogDto> Handshake,
    IReadOnlyList<SampleDto> Samples,
    IReadOnlyList<SnapshotDriftDto> Drift,
    IReadOnlyList<ApprovalDto> RecipeApprovals,
    IReadOnlyList<ProcessAlarmDto> Alarms,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<PlcWritePlanDto> WritePlan,
    string? ReleasedBy = null,
    DateTimeOffset? ReleasedAt = null,
    string? ReleaseComment = null,
    IReadOnlyList<BatchMaterialUseDto>? Materials = null,
    IReadOnlyList<LabSampleDto>? LabSamples = null);

public sealed record ProcessAlarmDto(
    Guid Id,
    Guid BatchId,
    string BatchNo,
    string StepCode,
    string Code,
    string Severity,
    string Message,
    DateTimeOffset RaisedAt,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgedBy);
