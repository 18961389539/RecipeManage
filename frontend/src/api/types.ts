export type UserRole = "Admin" | "ProcessEngineer" | "Supervisor" | "Quality" | "Operator";
export type RecipeStatus = "Draft" | "InReview" | "Approved" | "Rejected" | "Obsolete";
export type StepType = "Wait" | "Heat" | "Hold" | "Cool" | "Mix" | "Pressure" | "Transfer" | "QualityCheck" | "ManualConfirm";
export type BatchStatus = "Created" | "Queued" | "Running" | "Completed" | "Faulted" | "Aborted" | "Held" | "Released" | "DispositionRejected";
export type PlcProtocol = "Simulator" | "SiemensS7" | "ModbusTcp" | "OpcUa";

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
  parameters: ParameterDto[];
}

export interface EdgeDto {
  id?: string;
  fromStepId: string;
  toStepId: string;
}

export interface ApprovalDto {
  id: string;
  level: "Author" | "Supervisor" | "Quality";
  decision: "Pending" | "Approved" | "Rejected";
  reviewerName?: string | null;
  comment?: string | null;
  decidedAt?: string | null;
  meaning?: string | null;
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
}

export interface RecipeListItemDto {
  id: string;
  code: string;
  name: string;
  productCode: string;
  productName: string;
  lifecycle: string;
  approvedVersion?: number | null;
  draftStatus?: RecipeStatus | null;
  updatedAt: string;
  unitProcedures?: string[];
  pendingLevel?: "Author" | "Supervisor" | "Quality" | null;
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
}

export interface SnapshotStep {
  stepId: string;
  code: string;
  name: string;
  type: StepType;
  ordinal: number;
  watchdogSeconds: number;
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
  outcome: string;
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
  outcome: string;
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
}

export interface EquipmentClassDto {
  id: string;
  code: string;
  name: string;
  description?: string | null;
  templates: PhaseTemplateDto[];
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
  pendingLabSamples?: number;
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
