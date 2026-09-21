/**
 * 统一的枚举中文化与状态语义色。
 *
 * 背景：原先每个视图各自定义 statusLabel()，译法与配色不一致，
 * 且 Dashboard 大量直接渲染英文枚举（Running / Occupied / WaitingPlcReady），
 * 车间场景下可读性差。此模块作为唯一数据源供各视图复用。
 */

type Dict = Record<string, string>;

function translate(dict: Dict, value?: string | null, empty = "—") {
  if (value === null || value === undefined || value === "") return empty;
  return dict[value] ?? value;
}

export type TagType = "success" | "info" | "warning" | "danger" | "primary";

const batchStatusDict: Dict = {
  Created: "已创建",
  Queued: "排队",
  Running: "执行中",
  Completed: "待放行",
  Faulted: "故障",
  Aborted: "已中止",
  Held: "保持",
  Released: "已放行",
  DispositionRejected: "已拒收"
};

// 握手阶段既可能是 HandshakePhase 成员，也可能是批次状态串（详见 ProductionBatch / BatchLanes）。
const handshakePhaseDict: Dict = {
  WaitingPlcReady: "等待 PLC 就绪",
  WritingParameters: "下发参数",
  AwaitingPlcAck: "等待 PLC 应答",
  StepRunning: "工步执行中",
  Completing: "收尾确认",
  ReadyToAdvance: "可推进",
  Faulted: "握手故障",
  AwaitingConfirm: "等待人工确认",
  HostWait: "等待主控",
  Created: "已创建",
  Queued: "排队中",
  Held: "已暂停",
  Released: "已放行",
  Completed: "待放行",
  DispositionRejected: "已拒收"
};

const occupancyDict: Dict = { Occupied: "占用", Free: "空闲", Idle: "空闲", Empty: "空闲" };
const protocolDict: Dict = {
  Simulator: "仿真器",
  SiemensS7: "Siemens S7",
  ModbusTcp: "Modbus TCP",
  OpcUa: "OPC UA"
};
const userRoleDict: Dict = {
  Admin: "系统管理员",
  ProcessEngineer: "工艺工程师",
  Supervisor: "工艺主管",
  Quality: "质量工程师",
  Operator: "车间操作员"
};
const lotStatusDict: Dict = {
  Open: "在库",
  Consumed: "已消耗",
  Quarantine: "隔离",
  Released: "已放行",
  Void: "作废"
};
const lotSourceDict: Dict = { Received: "来料", Produced: "产出", Split: "拆分" };
const lotRoleDict: Dict = { Charge: "投料", Produced: "产出", Rework: "返工" };
const recipeStatusDict: Dict = {
  Draft: "草稿",
  InReview: "审核中",
  Approved: "生效",
  Rejected: "驳回",
  Obsolete: "历史"
};
const alarmSeverityDict: Dict = { Info: "提示", Warning: "警告", Fault: "故障", Critical: "严重" };
const labDispositionDict: Dict = { Pending: "待判定", Pass: "合格", Fail: "不合格", Void: "作废" };

export const batchStatusLabel = (v?: string | null) => translate(batchStatusDict, v);
export const handshakePhaseLabel = (v?: string | null) => translate(handshakePhaseDict, v);
export const occupancyLabel = (v?: string | null) => translate(occupancyDict, v);
export const protocolLabel = (v?: string | null) => translate(protocolDict, v);
export const userRoleLabel = (v?: string | null) => translate(userRoleDict, v);
export const lotStatusLabel = (v?: string | null) => translate(lotStatusDict, v);
export const lotSourceLabel = (v?: string | null) => translate(lotSourceDict, v);
export const lotRoleLabel = (v?: string | null) => translate(lotRoleDict, v);
export const recipeStatusLabel = (v?: string | null) => translate(recipeStatusDict, v);
export const alarmSeverityLabel = (v?: string | null) => translate(alarmSeverityDict, v);
const auditEntityDict: Dict = {
  MasterRecipe: "主配方",
  ProductionBatch: "生产批次",
  EquipmentLine: "设备"
};

const approvalLevelDict: Dict = {
  Author: "工艺工程师提交",
  Supervisor: "工艺主管",
  Quality: "质量"
};

const approvalDecisionDict: Dict = {
  Pending: "待签署",
  Approved: "已通过",
  Rejected: "已驳回"
};

// 工步执行结论（StepExecutionDto.outcome）。BatchMonitor 的时间线此前直接渲染 Pending / AwaitingConfirm 原文。
const stepOutcomeDict: Dict = {
  Pending: "待执行",
  Planned: "已排程",
  Queueing: "排队中",
  Running: "执行中",
  AwaitingConfirm: "等待人工确认",
  ReadyToAdvance: "可推进",
  Held: "已保持",
  Completed: "已完成",
  Skipped: "已跳过",
  Faulted: "故障",
  Aborted: "已中止"
};

export const stepOutcomeLabel = (v?: string | null) => translate(stepOutcomeDict, v);
export const stepOutcomeTagType = (v?: string | null): TagType => {
  switch (v) {
    case "Completed": return "success";
    case "Faulted":
    case "Aborted": return "danger";
    case "Held":
    case "AwaitingConfirm":
    case "Skipped": return "warning";
    case "Running": return "primary";
    default: return "info";
  }
};

export const approvalLevelLabel = (v?: string | null) => translate(approvalLevelDict, v);
export const approvalDecisionLabel = (v?: string | null) => translate(approvalDecisionDict, v);
export const approvalDecisionTagType = (v?: string | null): TagType =>
  v === "Approved" ? "success" : v === "Rejected" ? "danger" : "warning";

export const auditEntityLabel = (v?: string | null) => translate(auditEntityDict, v);
export const labDispositionLabel = (v?: string | null) => translate(labDispositionDict, v);

const batchStatusTagDict: Record<string, TagType> = {
  Created: "info",
  Queued: "warning",
  // 执行中用主色蓝而非绿色：一是绿色在本系统里保留给"已放行/已完成"这类好终态，
  // 二是工步级 stepOutcomeTagType 早就是 Running→primary，两级口径此前不一致。
  Running: "primary",
  // 待放行 = 工步全部跑完、只差 QA 放行，属于"成功结束"，此前与排队/保持同为黄色，
  // 列表里三种状态一个颜色，看不出哪个需要人动手。与工步级 Completed→success 对齐。
  Completed: "success",
  Faulted: "danger",
  Aborted: "danger",
  Held: "warning",
  Released: "success",
  DispositionRejected: "danger"
};

const recipeStatusTagDict: Record<string, TagType> = {
  Draft: "info",
  InReview: "warning",
  Approved: "success",
  Rejected: "danger",
  Obsolete: "info"
};

const lotStatusTagDict: Record<string, TagType> = {
  Open: "success",
  Consumed: "info",
  Quarantine: "warning",
  Released: "success",
  Void: "danger"
};

function tagType(dict: Record<string, TagType>, v?: string | null): TagType {
  return (v && dict[v]) || "info";
}

export const batchStatusTagType = (v?: string | null) => tagType(batchStatusTagDict, v);
export const recipeStatusTagType = (v?: string | null) => tagType(recipeStatusTagDict, v);
export const lotStatusTagType = (v?: string | null) => tagType(lotStatusTagDict, v);
export const occupancyTagType = (v?: string | null): TagType => (v === "Occupied" ? "warning" : "success");
export const alarmSeverityTagType = (v?: string | null): TagType =>
  v === "Critical" || v === "Fault" ? "danger" : v === "Warning" ? "warning" : "info";

/**
 * 采样点 tag → 中文名与工程单位。趋势图（ECharts 图例 / uPlot 图例 / 轴名）与实测区共用，
 * 同一测点在不同图上出现两种叫法会让操作员误以为是两个点。
 */
const signalDict: Record<string, { label: string; unit: string }> = {
  Temperature: { label: "温度", unit: "℃" },
  Pressure: { label: "压力", unit: "bar" },
  HoldTime: { label: "保温时长", unit: "s" }
};

export const signalLabel = (tag: string) => signalDict[tag]?.label ?? tag;
export const signalUnit = (tag: string) => signalDict[tag]?.unit ?? "";
