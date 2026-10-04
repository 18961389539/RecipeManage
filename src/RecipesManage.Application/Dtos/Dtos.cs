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
    string? PendingTitle = null,
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
    IReadOnlyList<RecipeVersionDto> Versions,
    string? ApprovalChainCode = null);

/// <summary>指定本配方走哪条审批链；null = 默认链。只影响之后的提交。</summary>
public sealed record UseApprovalChainRequest(string? Code, string Password);

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
    IReadOnlyList<ParameterDto> Parameters,
    int? PlcProgramId = null,
    string? EquipmentClassCode = null);

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
    bool ScaleWithBatch,
    ParameterSemantic Semantic = ParameterSemantic.Unspecified,
    string? MeasuredTag = null);

public sealed record EdgeDto(Guid Id, Guid FromStepId, Guid ToStepId);

public sealed record ApprovalDto(
    Guid Id,
    int Seq,
    ApprovalNode Node,
    string Title,
    UserRole RequiredRole,
    ApprovalDecision Decision,
    string? ReviewerName,
    string? Comment,
    DateTimeOffset? DecidedAt,
    string Meaning);

/// <summary>链上一个节点的可编辑定义。含义两句会随整条链冻结进审批记录，改配置不动在审版本。</summary>
public sealed record ApprovalChainStepDto(
    ApprovalNode Node,
    string Title,
    UserRole RequiredRole,
    string MeaningApproved,
    string MeaningRejected);

public sealed record ApprovalChainDto(
    Guid Id,
    string Code,
    string Name,
    bool IsDefault,
    bool Enabled,
    IReadOnlyList<ApprovalChainStepDto> Steps);

/// <summary>只需要电子签名（登录口令）就能执行的写操作用这个载荷。</summary>
public sealed record EsignPassword(string Password);

/// <summary>
/// 保存链时的节点定义。没有 Node：节点标识由 RequiredRole 派生（见 ApprovalNodeExtensions.NodeFor），
/// 它是历史行回填与缺省文案用的内部标识，不该变成管理员要理解的配置项。
/// </summary>
public sealed record ApprovalChainStepRequest(
    string Title,
    UserRole RequiredRole,
    string MeaningApproved,
    string MeaningRejected);

public sealed record SaveApprovalChainRequest(
    Guid? Id,
    string Code,
    string Name,
    bool IsDefault,
    bool Enabled,
    IReadOnlyList<ApprovalChainStepRequest> Steps,
    string Password);

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
    IReadOnlyList<SaveParameterRequest> Parameters,
    int? PlcProgramId = null,
    string? EquipmentClassCode = null);

public sealed record SaveParameterRequest(
    int SlotIndex,
    string Name,
    string EngineeringUnit,
    double Setpoint,
    double? Min,
    double? Max,
    bool WriteToPlc,
    bool ArchiveAsQuality,
    bool ScaleWithBatch = false,
    ParameterSemantic Semantic = ParameterSemantic.Unspecified,
    string? MeasuredTag = null);

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
public sealed record EsignActionRequest(
    string Password,
    string? Reason,
    Guid? StepId = null,
    int? EvidenceHashVersion = null,
    string? EvidenceHash = null);
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
    DateTimeOffset? StartedAt,
    bool PendingFinalSample = false);

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
    string? ReleaseComment = null,
    string? PendingHoldReason = null,
    string? PendingSkipReason = null,
    string? PendingConfirmComment = null);

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
    StepOutcome Outcome);

public sealed record StepExecutionDto(
    Guid StepId,
    string StepCode,
    string StepName,
    StepType StepType,
    int Ordinal,
    StepOutcome Outcome,
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

/// <summary>
/// 运行总览的计数。三条口径写死在这里，因为磁贴点进去就是对应的列表，数字不等就等于界面在说谎：
/// <paramref name="DraftRecipes"/> / <paramref name="ApprovedRecipes"/> / <paramref name="PendingApprovals"/>
/// 数的都是<strong>配方</strong>（不是版本行），<paramref name="PendingLabBatches"/> 数的是<strong>批次</strong>
/// （不是待判样品行，一个批次可能挂着多个）。
/// </summary>
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
    int PendingLabBatches = 0,
    int HeldBatches = 0,
    /// <summary>待放行批次里最早的创建时刻。数字只会变大，积压多久才知道该不该急；没有待放行时为 null。</summary>
    DateTimeOffset? OldestPendingReleaseAt = null,
    /// <summary>
    /// 最近 2 小时的批次重要事件（故障/保持/质检/转阶段/跳步/归档/复位/确认），
    /// 新的在前，最多 9 条。写参/触发/回读/等待这类微动作不上时间线——那是每步都有的心跳，
    /// 放进来会把真正要人看的事件淹没。
    /// </summary>
    IReadOnlyList<DashboardEventDto>? RecentEvents = null);

/// <summary>运行总览时间线里的一行。批次号在事件表里没有冗余，投影时连批表取。</summary>
public sealed record DashboardEventDto(
    DateTimeOffset At,
    string BatchNo,
    Guid BatchId,
    string StepCode,
    string Kind,
    string? Detail);

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
    string? Comment,
    LabSampleDispositionSignatureDto? DispositionSignature = null);

public sealed record LabSampleDispositionSignatureDto(
    string SignerName,
    DateTimeOffset At,
    string Meaning,
    string? Extra,
    int? ContentHashVersion,
    string? ContentHash,
    string Integrity);

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

/// <summary>
/// 批次 / 物料批 / 报警列表的服务端分页结果。
/// <paramref name="Total"/> 是筛选后的全量行数（不是本页行数），分页控件靠它算页数——
/// 以前接口只返回"最近 200 条"，前端既看不到更早的批次，排序也只在这 200 条里排。
/// </summary>
public sealed record BatchListPageDto(
    int Total,
    IReadOnlyList<BatchListItemDto> Items);

public sealed record MaterialLotPageDto(
    int Total,
    IReadOnlyList<MaterialLotDto> Items);

public sealed record ProcessAlarmPageDto(
    int Total,
    IReadOnlyList<ProcessAlarmDto> Items);

/// <summary>
/// 趋势样本：整批覆盖的取样结果。
///
/// 为什么不整表返回：样本是每约 400ms 每个测点一行，一个长跑批次能到几十万行，
/// 全量推给浏览器既慢又画不出来。取样在 SQL 侧按 <paramref name="step"/> 等间隔做，
/// 所以返回的行数只由 <paramref name="MaxPoints"/> 决定，与批次跑多久无关，
/// 而且<strong>覆盖整批</strong>（早期实现是"读最近 5 万行再抽稀"，那会把趋势截成最近两三小时）。
/// <paramref name="Total"/> 仍是原始行数，界面据此写明"每 N 条取 1 点"。
/// 原始样本一行都不删：抽稀只发生在读的一侧，电子批记录仍用全量数据。
/// </summary>
public sealed record SampleSeriesDto(
    IReadOnlyList<SampleDto> Points,
    int Total,
    int Step,
    int MaxPoints);

/// <summary>
/// 握手履历的一页。<paramref name="Total"/> 是该批次的全部条数——
/// 监控页只回最近若干条，必须说清楚，否则用户会以为履历就这么多。
/// 完整履历在电子批记录里，那里不截。
/// </summary>
public sealed record HandshakeLogPageDto(
    int Total,
    IReadOnlyList<HandshakeLogDto> Items);

/// <summary>一份备份文件。<paramref name="CreatedAt"/> 是写入时刻（UTC），不是文件系统的修改时间。</summary>
public sealed record BackupFileDto(string Name, long Bytes, DateTimeOffset CreatedAt);

/// <summary>
/// 备份现状：配置 + 磁盘上有哪些份 + 本进程最近一次跑的结果。
/// <paramref name="LastError"/> 只在本进程内有过失败时非空，历史失败在操作审计里查 system.backup.failed。
/// </summary>
public sealed record BackupStatusDto(
    string Directory,
    bool Enabled,
    int Keep,
    string AtUtc,
    DateTimeOffset NextRunAtUtc,
    IReadOnlyList<BackupFileDto> Files,
    DateTimeOffset? LastRunAtUtc,
    string? LastFile,
    string? LastError);

/// <summary>
/// 一轮 SQLite 维护的结果。<paramref name="SkippedReason"/> 非空表示 VACUUM 没做以及为什么——
/// 那是安全判断（批次在跑 / 空闲页不值得重写整个文件），不是错误。
/// </summary>
public sealed record MaintenanceResultDto(
    bool Optimized,
    bool Vacuumed,
    long BytesBefore,
    long BytesAfter,
    long ReclaimedBytes,
    string? SkippedReason,
    string Detail);

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
    bool ScaleWithBatch,
    ParameterSemantic Semantic = ParameterSemantic.Unspecified,
    string? MeasuredTag = null);

public sealed record PhaseTemplateDto(
    Guid Id,
    string Code,
    string Name,
    StepType StepType,
    string Operation,
    int WatchdogSeconds,
    string ClassCode,
    IReadOnlyList<PhaseParameterDto> Parameters,
    int? PlcProgramId = null);

public sealed record EquipmentClassDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    IReadOnlyList<PhaseTemplateDto> Templates);

public sealed record UpsertPhaseTemplateRequest(
    string Code,
    string Name,
    StepType StepType,
    int PlcProgramId,
    int WatchdogSeconds,
    string? Operation,
    IReadOnlyList<PhaseParameterDto> Parameters);

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
    IReadOnlyList<LabSampleDto>? LabSamples = null,
    IReadOnlyList<BatchEsignDto>? Esigns = null,
    int? EvidenceHashVersion = null,
    string? EvidenceHash = null);

public sealed record BatchEsignDto(
    string Action,
    string Meaning,
    string? UserName,
    DateTimeOffset At,
    string? Extra,
    int? ContentHashVersion = null,
    string? ContentHash = null,
    string? Integrity = null);

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
