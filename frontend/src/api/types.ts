export type UserRole = "Admin" | "ProcessEngineer" | "Supervisor" | "Quality" | "Operator";
export type RecipeStatus = "Draft" | "InReview" | "Approved" | "Rejected" | "Obsolete";
export type StepType = "Wait" | "Heat" | "Hold" | "Cool" | "Mix" | "Pressure" | "Transfer" | "QualityCheck" | "ManualConfirm";
export type BatchStatus = "Created" | "Queued" | "Running" | "Completed" | "Faulted" | "Aborted" | "Held" | "Released" | "DispositionRejected";
export type PlcProtocol = "Simulator" | "SiemensS7" | "ModbusTcp" | "OpcUa";
/** 工步执行结论。成员就是 batch_step_executions / batch_lanes 里存的 TEXT 值。 */
export type StepOutcome = "Pending" | "Running" | "Held" | "AwaitingConfirm" | "Completed" | "Skipped" | "Faulted";
/** 主配方的生命周期：Obsolete 只读归档，与下面的 RecipeStatus（版本状态）不是一回事。 */
export type RecipeLifecycle = "Active" | "Obsolete";
/**
 * 参数语义。Unspecified 时后端按名称/单位推断（历史数据的回退路径），
 * 显式声明后归档取哪个实测点、哪个参数是工艺时长都由它说了算。
 */
export type ParameterSemantic = "Unspecified" | "Duration" | "Rate";

export interface UserDto {
  id: string;
  userName: string;
  displayName: string;
  role: UserRole;
  isActive?: boolean;
}

export interface ParameterDto {
  id?: string;
  slotIndex: number;
  name: string;
  engineeringUnit: string;
  setpoint: number;
  min?: number | null;
  max?: number | null;
  writeToPlc: boolean;
  archiveAsQuality: boolean;
  scaleWithBatch?: boolean;
  semantic?: ParameterSemantic;
  /** 归档时读取的实测点键名，对应设备点表 Measured 的 key；空则由后端按名称/单位推断。 */
  measuredTag?: string | null;
}

export interface StepDto {
  id: string;
  code: string;
  name: string;
  type: StepType;
  ordinal: number;
  canvasX: number;
  canvasY: number;
  watchdogSeconds: number;
  description?: string | null;
  unitProcedure?: string | null;
  operation?: string | null;
  plcProgramId?: number | null;
  equipmentClassCode?: string | null;
  parameters: ParameterDto[];
}

export interface EdgeDto {
  id?: string;
  fromStepId: string;
  toStepId: string;
}

/** 审批节点的稳定标识。链是可配的数据，界面显示一律用 title，这里只作筛选与兜底。 */
export type ApprovalNode = "Submission" | "Supervisor" | "Quality" | "Release";

export interface ApprovalDto {
  id: string;
  /** 链上顺序，0 是提交动作。推进与"还差谁签"只看它。 */
  seq: number;
  node: ApprovalNode;
  /** 提交时冻结在记录上的节点名称——链后来怎么改都不影响这一版显示什么。 */
  title: string;
  requiredRole: UserRole;
  decision: "Pending" | "Approved" | "Rejected";
  reviewerName?: string | null;
  comment?: string | null;
  decidedAt?: string | null;
  meaning?: string | null;
}

export interface ApprovalChainStepDto {
  node: ApprovalNode;
  title: string;
  requiredRole: UserRole;
  meaningApproved: string;
  meaningRejected: string;
}

/** 保存时的节点定义：没有 node，标识由 requiredRole 派生（那是内部标识，不该让管理员理解）。 */
export interface ApprovalChainStepRequest {
  title: string;
  requiredRole: UserRole;
  meaningApproved: string;
  meaningRejected: string;
}

export interface ApprovalChainDto {
  id: string;
  code: string;
  name: string;
  isDefault: boolean;
  enabled: boolean;
  steps: ApprovalChainStepDto[];
}

export interface RecipeVersionDto {
  id: string;
  versionNumber: number;
  status: RecipeStatus;
  changeNote?: string | null;
  submittedAt?: string | null;
  approvedAt?: string | null;
  steps: StepDto[];
  edges: EdgeDto[];
  approvals: ApprovalDto[];
}

export interface RecipeDetailDto {
  id: string;
  code: string;
  name: string;
  productCode: string;
  productName: string;
  description?: string | null;
  draft?: RecipeVersionDto | null;
  approved?: RecipeVersionDto | null;
  versions: RecipeVersionDto[];
  /** 本配方走哪条审批链；null = 默认链。 */
  approvalChainCode?: string | null;
}

export interface RecipeListItemDto {
  id: string;
  code: string;
  name: string;
  productCode: string;
  productName: string;
  lifecycle: RecipeLifecycle;
  approvedVersion?: number | null;
  draftStatus?: RecipeStatus | null;
  updatedAt: string;
  unitProcedures?: string[];
  /** 待决节点的冻结名称；链可配，所以这里不是枚举而是文本。 */
  pendingTitle?: string | null;
  pendingMeaning?: string | null;
  reviewVersion?: number | null;
}

export interface BatchListItemDto {
  id: string;
  batchNo: string;
  recipeName: string;
  recipeVersion: number;
  equipmentCode: string;
  productName: string;
  status: BatchStatus;
  handshakePhase: string;
  currentStepIndex: number;
  createdAt: string;
  startedAt?: string | null;
  pendingFinalSample?: boolean;
}

export interface SnapshotParameter {
  slotIndex: number;
  name: string;
  engineeringUnit: string;
  setpoint: number;
  min?: number | null;
  max?: number | null;
  writeToPlc: boolean;
  archiveAsQuality: boolean;
  scaleWithBatch?: boolean;
  semantic?: ParameterSemantic;
  /** 归档时读取的实测点键名，对应设备点表 Measured 的 key；空则由后端按名称/单位推断。 */
  measuredTag?: string | null;
}

export interface SnapshotStep {
  stepId: string;
  code: string;
  name: string;
  type: StepType;
  ordinal: number;
  watchdogSeconds: number;
  plcProgramId?: number | null;
  unitProcedure?: string | null;
  operation?: string | null;
  parameters: SnapshotParameter[];
}

export interface SnapshotEdge {
  fromStepId: string;
  toStepId: string;
}

export interface ControlRecipeSnapshot {
  masterRecipeId: string;
  recipeVersionId: string;
  versionNumber: number;
  recipeCode: string;
  recipeName: string;
  productCode: string;
  productName: string;
  frozenAt: string;
  integrityHash?: string | null;
  scaleFactor?: number | null;
  lotNumber?: string | null;
  unitEquipment?: Record<string, string> | null;
  steps: SnapshotStep[];
  edges?: SnapshotEdge[];
}

export interface StepExecutionDto {
  stepId: string;
  stepCode: string;
  stepName: string;
  stepType: StepType;
  ordinal: number;
  outcome: StepOutcome;
  startedAt?: string | null;
  completedAt?: string | null;
  qualityJson?: string | null;
}

export interface LaneHandshakeDto {
  equipmentCode: string;
  equipmentId: string;
  unitProcedure: string;
  stepId?: string | null;
  stepCode: string;
  phase: string;
  outcome: StepOutcome;
}

export interface BatchDetailDto {
  id: string;
  batchNo: string;
  status: BatchStatus;
  handshakePhase: string;
  faultCode?: string | null;
  faultMessage?: string | null;
  equipmentId: string;
  equipmentName: string;
  snapshot: ControlRecipeSnapshot;
  currentStepId?: string | null;
  currentStepIndex: number;
  stepExecutions: StepExecutionDto[];
  lanes?: LaneHandshakeDto[];
  createdAt: string;
  startedAt?: string | null;
  completedAt?: string | null;
  snapshotIntegrity: string;
  writePlan?: PlcWritePlanDto[];
  releasedBy?: string | null;
  releasedAt?: string | null;
  releaseComment?: string | null;
  pendingHoldReason?: string | null;
  pendingSkipReason?: string | null;
  pendingConfirmComment?: string | null;
}

export interface PlcWritePlanDto {
  stepId: string;
  stepCode: string;
  stepName: string;
  stepType: StepType;
  plcStepId: number;
  plcStepType: number;
  parameters: number[];
  writeToPlc: boolean;
  policy: string;
}

export interface SampleDto {
  sampledAt: string;
  tag: string;
  value: number;
  unit?: string | null;
  stepId?: string | null;
}

export interface PhaseParameterDto {
  slotIndex: number;
  name: string;
  engineeringUnit: string;
  setpoint: number;
  min?: number | null;
  max?: number | null;
  writeToPlc: boolean;
  archiveAsQuality: boolean;
  scaleWithBatch?: boolean;
  semantic?: ParameterSemantic;
  /** 归档时读取的实测点键名，对应设备点表 Measured 的 key；空则由后端按名称/单位推断。 */
  measuredTag?: string | null;
}

export interface PhaseTemplateDto {
  id: string;
  code: string;
  name: string;
  stepType: StepType;
  operation: string;
  watchdogSeconds: number;
  classCode: string;
  parameters: PhaseParameterDto[];
  plcProgramId?: number | null;
}

export interface EquipmentClassDto {
  id: string;
  code: string;
  name: string;
  description?: string | null;
  templates: PhaseTemplateDto[];
}

export interface UpsertEquipmentRequest {
  code: string;
  name: string;
  protocol: PlcProtocol;
  host: string;
  port: number;
  plcModel: string;
  rack: number;
  slot: number;
  enabled: boolean;
  tagMapJson: string;
  description?: string | null;
  watchdogJson?: string | null;
  equipmentClassCode?: string | null;
}

export interface UpsertPhaseTemplateRequest {
  code: string;
  name: string;
  stepType: StepType;
  plcProgramId: number;
  watchdogSeconds: number;
  operation?: string | null;
  parameters: PhaseParameterDto[];
}

export interface EquipmentDto {
  id: string;
  code: string;
  name: string;
  protocol: PlcProtocol;
  host: string;
  port: number;
  plcModel: string;
  rack: number;
  slot: number;
  enabled: boolean;
  tagMapJson: string;
  description?: string | null;
  watchdogJson?: string | null;
  occupancy?: string;
  occupyingBatchNo?: string | null;
  occupyingBatchId?: string | null;
  equipmentClassCode?: string | null;
}

export interface EquipmentOccupancyDto {
  equipmentId: string;
  code: string;
  name: string;
  protocol: string;
  enabled: boolean;
  occupancy: string;
  batchId?: string | null;
  batchNo?: string | null;
  batchStatus?: string | null;
  handshakePhase?: string | null;
}

/**
 * 运行总览的计数。三个配方类计数与 pendingLabBatches 的口径都写在后端 DashboardDto 的注释里，
 * 一句话版本：磁贴数的是"点进去那个列表会有多少条"，所以配方类数配方、待检终样数批次。
 */
export interface DashboardDto {
  runningBatches: number;
  queuedBatches: number;
  draftRecipes: number;
  pendingApprovals: number;
  approvedRecipes: number;
  faultedBatches: number;
  openAlarms?: number;
  liveBatches: BatchListItemDto[];
  equipmentOccupancy?: EquipmentOccupancyDto[];
  pendingReleaseBatches?: number;
  pendingLabBatches?: number;
  heldBatches?: number;
}

export interface AuditLogDto {
  id: string;
  userName: string;
  action: string;
  entityType: string;
  entityId: string;
  detail?: string | null;
  at: string;
}

export interface AuditLogPageDto {
  total: number;
  items: AuditLogDto[];
}

/** 服务端分页的列表信封：total 是筛选后的全量行数，不是本页行数。 */
export interface BatchListPageDto {
  total: number;
  items: BatchListItemDto[];
}

export interface MaterialLotPageDto {
  total: number;
  items: MaterialLotDto[];
}

export interface ProcessAlarmPageDto {
  total: number;
  items: ProcessAlarmDto[];
}

/** 一份留在服务器上的日常快照。name 里的时间是定宽 UTC，字典序即时间序。 */
export interface BackupFileDto {
  name: string;
  bytes: number;
  createdAt: string;
}

/**
 * 备份现状。lastError 只反映本进程这一轮，历史失败去操作审计查 system.backup.failed。
 */
export interface BackupStatusDto {
  directory: string;
  enabled: boolean;
  keep: number;
  atUtc: string;
  nextRunAtUtc: string;
  files: BackupFileDto[];
  lastRunAtUtc?: string | null;
  lastFile?: string | null;
  lastError?: string | null;
}

/**
 * 一轮 SQLite 维护的结果。skippedReason 非空表示 VACUUM 没做以及为什么——
 * 那是安全判断（批次在跑、或空闲页不值得重写整个文件），不是错误。
 */
export interface MaintenanceResultDto {
  optimized: boolean;
  vacuumed: boolean;
  bytesBefore: number;
  bytesAfter: number;
  reclaimedBytes: number;
  skippedReason?: string | null;
  detail: string;
}

/**
 * 趋势样本：points 是覆盖**整批**的等间隔取样结果，total 是原始行数，step 是取样步长。
 * step > 1 时界面必须写明口径——取样只发生在读的一侧，原始样本一行都不删，批记录仍用全量。
 */
export interface SampleSeriesDto {
  points: SampleDto[];
  total: number;
  step: number;
  maxPoints: number;
}

/** 握手履历的一页：total 是该批次的全量条数，被截时界面必须说明。 */
export interface HandshakeLogPageDto {
  total: number;
  items: HandshakeLogDto[];
}

export interface RecipeFieldChangeDto {
  path: string;
  before?: string | null;
  after?: string | null;
}

export interface RecipeVersionDiffDto {
  fromVersion: number;
  toVersion: number;
  addedSteps: string[];
  removedSteps: string[];
  changes: RecipeFieldChangeDto[];
  changedStepCodes?: string[];
}

export interface RecipePackageDto {
  exportedAt: string;
  database: string;
  recipes: RecipeDetailDto[];
}

export interface RecipeImportResultDto {
  created: number;
  skipped: number;
  messages: string[];
}

export interface TagMapCheckDto {
  result: string;
  message: string;
}

export interface ConnectionTestDto {
  connected: boolean;
  plcReady: boolean;
  latencyMs: number;
  protocol: string;
  message: string;
}

export interface HandshakeLogDto {
  at: string;
  stepCode: string;
  phase: string;
  kind: string;
  detail?: string | null;
  remainingSeconds?: number | null;
}

export interface SnapshotDriftDto {
  stepCode: string;
  parameter: string;
  frozenSetpoint: number;
  masterSetpoint?: number | null;
  drifted: boolean;
}

export interface ExecutionEvent {
  batchId: string;
  type: string;
  payload: unknown;
}

export interface HealthDto {
  status: string;
  database?: string;
  engine?: string;
  controlRecipe?: string;
}

export interface BatchRecordDto {
  batchId: string;
  batchNo: string;
  status: BatchStatus;
  snapshotIntegrity: string;
  snapshot: ControlRecipeSnapshot;
  stepExecutions: StepExecutionDto[];
  handshake: HandshakeLogDto[];
  samples: SampleDto[];
  drift: SnapshotDriftDto[];
  recipeApprovals: ApprovalDto[];
  alarms?: ProcessAlarmDto[];
  generatedAt: string;
  writePlan?: PlcWritePlanDto[];
  releasedBy?: string | null;
  releasedAt?: string | null;
  releaseComment?: string | null;
  materials?: BatchMaterialUseDto[];
  labSamples?: LabSampleDto[];
  esigns?: BatchEsignDto[];
}

export interface BatchEsignDto {
  action: string;
  meaning: string;
  userName?: string | null;
  at: string;
  extra?: string | null;
}

export type MaterialLotSource = "Received" | "Produced" | "Split";
export type MaterialLotStatus = "Open" | "Consumed" | "Quarantine" | "Released" | "Void";
export type MaterialUseRole = "Charge" | "Produced" | "Rework";
export type LabSampleType = "InProcess" | "Final" | "Retain";
export type LabSampleDisposition = "Pending" | "Pass" | "Fail" | "Void";

export interface MaterialLotDto {
  id: string;
  lotNumber: string;
  materialCode: string;
  materialName: string;
  parentLotId?: string | null;
  source: MaterialLotSource;
  status: MaterialLotStatus;
  quantity?: number | null;
  uom?: string | null;
  producedBatchId?: string | null;
  createdAt: string;
}

export interface BatchMaterialUseDto {
  id: string;
  batchId: string;
  batchNo?: string | null;
  materialLotId: string;
  lotNumber: string;
  materialCode: string;
  role: MaterialUseRole;
  quantity?: number | null;
  stepId?: string | null;
}

export interface LabSampleDto {
  id: string;
  sampleCode: string;
  batchId: string;
  materialLotId?: string | null;
  lotNumber?: string | null;
  parentSampleId?: string | null;
  stepId?: string | null;
  sampleType: LabSampleType;
  disposition: LabSampleDisposition;
  resultsJson?: string | null;
  takenBy: string;
  takenAt: string;
  dispositionBy?: string | null;
  disposedAt?: string | null;
  comment?: string | null;
}

export interface LotGenealogyDto {
  lot: MaterialLotDto;
  ancestors: MaterialLotDto[];
  descendants: MaterialLotDto[];
  uses: BatchMaterialUseDto[];
}

export interface ProcessAlarmDto {
  id: string;
  batchId: string;
  batchNo: string;
  stepCode: string;
  code: string;
  severity: string;
  message: string;
  raisedAt: string;
  acknowledgedAt?: string | null;
  acknowledgedBy?: string | null;
}
