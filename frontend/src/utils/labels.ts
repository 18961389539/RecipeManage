/**
 * 统一的枚举中文化与状态语义色。
 *
 * 背景：原先每个视图各自定义 statusLabel()，译法与配色不一致，
 * 且 Dashboard 大量直接渲染英文枚举（Running / Occupied / WaitingPlcReady），
 * 车间场景下可读性差。此模块作为唯一数据源供各视图复用。
 */

import { parseHandshakeSummary } from "./handshake";
import { t } from "../i18n";
import type { ParameterSemantic } from "../api/types";

type Dict = Record<string, string>;

/**
 * 全站标签的唯一出口，所以 i18n 也只在这里接一次：字典继续存中文（那是键与缺省显示），
 * 取用时过一层 t()。别在视图里另写一份翻译，也别把字典值改成英文——中文原文就是键。
 * 缺译时 t() 原样返回中文，界面退化可预期。
 */
function translate(dict: Dict, value?: string | null, empty = "—") {
  if (value === null || value === undefined || value === "") return t(empty);
  return t(dict[value] ?? value);
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

// 握手列只显示 HandshakePhase / 上位机叠加态。旧库里曾把待放行/已放行写进这一列，仍能翻译。
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
  Held: "已暂停",
  Created: "已创建",
  Queued: "排队中",
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

/**
 * 枚举的业务次序（列表排序用，不是字母序也不是拼音序）：
 * 与状态机推进方向一致，这样「点一次状态列」得到的是同一阶段的批次聚在一起，
 * 而不是 DispositionRejected 排在 Created 前面的英文字典序。
 * 未列出的值落到队尾，与 labels 的降级翻译（原样显示）保持一致。
 */
export const batchStatusOrder = [
  "Created", "Queued", "Running", "Held", "Completed", "DispositionRejected", "Released", "Faulted", "Aborted"
];
export const recipeStatusOrder = ["Draft", "InReview", "Rejected", "Approved", "Obsolete"];
export const lotStatusOrder = ["Open", "Quarantine", "Released", "Consumed", "Void"];
export const lotSourceOrder = ["Received", "Produced", "Split"];
// 报警按「越严重越靠前」排，所以次序从最重开始。
export const alarmSeverityOrder = ["Critical", "Fault", "Warning", "Info"];
// 排序用次序，不是状态机次序：Occupied 在前（要抢设备的人先看），Idle/Empty 是 occupancyDict 里的同义写法。
export const occupancyOrder = ["Occupied", "Free", "Idle", "Empty"];
export const userRoleOrder = ["Admin", "ProcessEngineer", "Supervisor", "Quality", "Operator"];

export const batchStatusLabel = (v?: string | null) => translate(batchStatusDict, v);
export const handshakePhaseLabel = (v?: string | null) => {
  const parts = parseHandshakeSummary(v);
  if (parts.length > 1)
    return parts.map((p) => `${p.equipment}:${translate(handshakePhaseDict, p.phase)}`).join(" · ");
  if (parts.length === 1 && parts[0].equipment)
    return `${parts[0].equipment}:${translate(handshakePhaseDict, parts[0].phase)}`;
  return translate(handshakePhaseDict, v);
};

/** HelpTip 用术语表 key：单相位用中文名，多车道汇总用「四步握手」。 */
export const handshakePhaseTip = (v?: string | null) => {
  const parts = parseHandshakeSummary(v);
  if (parts.length > 1) return "四步握手";
  return handshakePhaseLabel(parts[0]?.phase || v);
};
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
  RecipeVersion: "配方版本",
  ProductionBatch: "生产批次",
  EquipmentLine: "设备",
  AppUser: "用户",
  MaterialLot: "物料批",
  LabSample: "实验室样品",
  ProcessAlarm: "过程报警",
  PhaseTemplate: "相模板",
  System: "系统"
};

// 审批节点名称现在是数据（链上配的），这里只留标识的中文兜底：
// 历史行、下拉候选、以及名称没填的极端情况用它，正常展示一律用记录上冻结的 title。
const approvalNodeDict: Dict = {
  Submission: "提交",
  Supervisor: "工艺主管",
  Quality: "质量审核",
  Release: "出厂放行"
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

export const approvalNodeLabel = (v?: string | null) => translate(approvalNodeDict, v);
export const approvalDecisionLabel = (v?: string | null) => translate(approvalDecisionDict, v);
export const approvalDecisionTagType = (v?: string | null): TagType =>
  v === "Approved" ? "success" : v === "Rejected" ? "danger" : "warning";

export const auditEntityLabel = (v?: string | null) => translate(auditEntityDict, v);
export const labDispositionLabel = (v?: string | null) => translate(labDispositionDict, v);

const handshakeKindDict: Dict = {
  write: "写参",
  trigger: "触发",
  verify: "回读",
  wait: "等待",
  confirm: "确认",
  quality: "质检",
  hold: "保持",
  skip: "跳步",
  fault: "故障",
  phase: "转阶段",
  archive: "归档",
  reset: "复位",
  advance: "推进",
  resume: "恢复"
};

const stepTypeDict: Dict = {
  Heat: "升温",
  Hold: "保温",
  Cool: "冷却",
  Mix: "搅拌",
  Pressure: "加压",
  QualityCheck: "质检",
  Wait: "等待",
  Transfer: "转移",
  ManualConfirm: "人工确认"
};

const executionKindDict: Dict = {
  WritePlc: "写 PLC",
  Wait: "等待",
  QualityCheck: "质检",
  ManualConfirm: "人工确认"
};

const parameterSemanticDict: Dict = {
  Unspecified: "未声明",
  Duration: "工艺时长",
  Rate: "速率"
};

/** 下拉顺序：未声明排第一，因为历史参数都是这个值。 */
export const parameterSemanticOptions: ParameterSemantic[] = ["Unspecified", "Duration", "Rate"];

const labSampleTypeDict: Dict = {
  InProcess: "过程样",
  Final: "终检样",
  Retain: "留样"
};

const databaseDict: Dict = {
  sqlite: "SQLite"
};

const auditActionDict: Dict = {
  "batch.create": "创建批次",
  "batch.start.esign": "电子签名启动",
  "batch.retry.esign": "电子签名重新排队",
  "batch.abort.esign": "电子签名中止",
  "batch.hold.esign": "电子签名保持",
  "batch.resume.esign": "电子签名恢复",
  "batch.skip.esign": "电子签名跳步",
  "batch.confirm.esign": "电子签名确认工步",
  "batch.release.esign": "电子签名放行",
  "batch.reject.esign": "电子签名拒收",
  "batch.record.pdf": "导出电子批记录",
  "batch.fault": "握手故障",
  "alarm.ack": "确认报警",
  "recipe.create": "创建配方",
  "recipe.header": "修改配方抬头",
  "recipe.procedure.esign": "电子签名保存工艺",
  "recipe.submit.esign": "电子签名提交审核",
  "recipe.decide.esign": "电子签名审核",
  "recipe.reopen.esign": "电子签名重新打开",
  "recipe.new-version.esign": "电子签名升版",
  "recipe.import": "导入配方",
  "recipe.autofix.heat-duration": "自动补升温时长",
  "recipe.autofix.mojibake": "自动修复乱码字段",
  "recipe.autofix.isa88": "自动补 ISA-88 单元",
  "recipe.autofix.recipe-unit-class": "自动回填设备类",
  "recipe.autofix.recipe-unit-class-host": "自动继承设备类",
  "equipment.autofix.hold-tags": "自动补保持握手点表",
  "equipment.autofix.plc-program-templates": "自动补相模板与程序号",
  "equipment.create": "创建设备",
  "equipment.update": "更新设备",
  "equipment.inject-fault": "注入仿真故障",
  "equipment.template.create": "新增相模板",
  "equipment.template.update": "更新相模板",
  "equipment.template.delete": "删除相模板",
  "user.create": "创建用户",
  "user.update": "更新用户",
  "auth.login": "登录成功",
  "auth.login.failed": "登录失败",
  "auth.login.locked": "登录锁定",
  "admin.sqlite-backup": "下载整库备份",
  // 定时任务的作者是 system，不是某个账号——审计里要能分清"人取走了一份"和"机器落了一份"。
  "system.backup": "数据库备份",
  "system.backup.failed": "数据库备份失败",
  "system.maintenance": "数据库维护",
  "lot.receive": "来料登记",
  "lot.split": "拆分子批",
  "lab.sample.create": "登记样品",
  "lab.sample.dispose.esign": "电子签名判定样品"
};

export const handshakeKindLabel = (v?: string | null) => translate(handshakeKindDict, v);
export const stepTypeLabel = (v?: string | null) => translate(stepTypeDict, v);

const hostTypeSet = new Set(["Wait", "QualityCheck", "ManualConfirm"]);
const defaultProgramByType: Dict = {
  Wait: "0", Heat: "1", Hold: "2", Cool: "3", Mix: "4", Pressure: "5", Transfer: "6",
  QualityCheck: "7", ManualConfirm: "8"
};

function operationTail(operation?: string | null) {
  const op = operation?.trim();
  if (!op) return "";
  const i = op.lastIndexOf(" ");
  return (i >= 0 ? op.slice(i + 1) : op).trim();
}

/**
 * 界面「类型」：自定义相显示相名（水冲洗），不要用枚举别名（转移）。
 * 上位机工步仍用等待/质检/人工确认。
 */
export function phaseTypeLabel(step?: {
  type?: string | null;
  stepType?: string | null;
  name?: string | null;
  operation?: string | null;
  plcProgramId?: number | null;
} | null) {
  const type = step?.type ?? step?.stepType ?? "";
  if (!type) return "—";
  if (hostTypeSet.has(type)) return stepTypeLabel(type);
  const alias = stepTypeLabel(type);
  const name = step?.name?.trim() ?? "";
  const tail = operationTail(step?.operation);
  const program = step?.plcProgramId;
  const def = Number(defaultProgramByType[type]);
  const custom = program != null && Number.isFinite(def) && program !== def;
  if (custom && name) return name;
  if (tail && tail !== alias) return tail;
  return alias;
}
export const executionKindLabel = (v?: string | null) => translate(executionKindDict, v);
export const parameterSemanticLabel = (v?: string | null) => translate(parameterSemanticDict, v, "未声明");
export const labSampleTypeLabel = (v?: string | null) => translate(labSampleTypeDict, v);
export const databaseLabel = (v?: string | null) => translate(databaseDict, v, "SQLite");
export const auditActionLabel = (v?: string | null) => translate(auditActionDict, v);

/** 与后端 ElectronicSignature.Batch 逐字对齐；弹窗与 eBR 共用，禁止在视图里另写一份。 */
const esignMeaningDict: Dict = {
  "batch.start.esign": "我作为操作员确认控制配方快照完整有效，启动本批四步握手，禁止盲写。",
  "batch.retry.esign": "我作为操作员确认故障已排除，从当前工步重新排队并恢复握手。",
  "batch.abort.esign": "我作为操作员确认中止本批，停止写参并释放设备占用。",
  "batch.hold.esign": "我作为操作员确认请求保持：写 Host_Hold，等待 PLC_Held，禁止盲写下一步。",
  "batch.resume.esign": "我作为操作员确认解除保持，从当前工步继续四步握手。",
  "batch.skip.esign": "我作为主管确认跳过当前工步：仅在未写参的就绪/等待/确认相位，禁止跨阶段盲写。",
  "batch.confirm.esign": "我作为操作员确认本工步人工确认点已核对，允许继续且本工步不写 PLC。",
  "batch.release.esign": "我作为质量审核人对照归档质检与四步握手，批准本批放行。",
  "batch.reject.esign": "我作为质量审核人对照归档质检与四步握手，拒收本批。",
  "lab.sample.dispose.esign": "我作为质量审核人对照规格判定本样品。",
  // 配方侧的签署含义原先抄在设计器视图里（5 处长文本），现在与批次侧同一个出口：
  // 视图只报动作码，文案在这里唯一维护。
  "recipe.procedure.esign": "GxP：保存会改写草稿 Procedure / Steps / Parameters。签署含义：我作为工艺工程师确认本次变更准确，并记录变更原因。",
  "recipe.submit.esign": "签署含义：我作为工艺工程师确认本版本 Procedure / Steps 与 Parameters / Setpoints 准确，提交多级审核。",
  "recipe.reopen.esign": "签署含义：我作为工艺工程师确认将被驳回版本重新打开为草稿，并继续修订 Procedure / Setpoints。",
  "recipe.new-version.esign": "签署含义：我作为工艺工程师确认基于当前生效版本另开草稿，本条变更说明会写进新版本履历。",
  // 整库备份不是日常动作，含义里必须写清"导出的是什么"，否则签名的人以为只是下载个报表。
  "admin.sqlite-backup": "我导出的是整库备份，包含全部账号口令哈希与全部批记录，须按含敏感数据的介质保管。"
};

export const esignMeaning = (action?: string | null) =>
  translate(esignMeaningDict, action, "请再次输入登录密码作为电子签名。");

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

// 图例走的是这个函数而不是 translate()，所以同样要过一层 t()，否则趋势图在英文界面仍是中文。
export const signalLabel = (tag: string) => t(signalDict[tag]?.label ?? tag);
export const signalUnit = (tag: string) => signalDict[tag]?.unit ?? "";
