import type { StepType } from "../api/types";
import { t } from "../i18n";
import { isHostStepType, resolvePlcProgram } from "./plcProgram";
import { unitLane } from "../procedureLayout";

/**
 * 工艺拓扑规则（纯函数，不碰 ref、不弹提示）。
 *
 * 这些规则决定"两条工步之间能不能画一条线"以及"这份草稿能不能保存"，
 * 留在视图里时它们和 ElMessage 混在一起，既没法单测也没法被别的入口复用
 * （设计器的拖拽连线、下拉添加连线、"汇合到当前工步"三个入口用的是同一条规则）。
 * 校验函数返回**要展示的文案**而不是布尔值，调用方只决定用什么控件呈现。
 */

export interface TopoStep {
  id: string;
  code: string;
  ordinal: number;
  type: StepType;
  unitProcedure?: string | null;
  operation?: string | null;
  plcProgramId?: number | null;
}

export interface TopoEdge {
  fromStepId: string;
  toStepId: string;
}

export const JOIN_RULE_HINT = "跨单元连线必须从该单元末工步进入目标单元首工步；同单元必须按工步顺序";

function laneTail(steps: TopoStep[], lane: string) {
  return [...steps]
    .filter((s) => unitLane(s.unitProcedure) === lane)
    .sort((a, b) => a.ordinal - b.ordinal || a.code.localeCompare(b.code))
    .at(-1);
}

function laneHead(steps: TopoStep[], lane: string) {
  return [...steps]
    .filter((s) => unitLane(s.unitProcedure) === lane)
    .sort((a, b) => a.ordinal - b.ordinal || a.code.localeCompare(b.code))[0];
}

export function canJoinSteps(steps: TopoStep[], from: TopoStep, to: TopoStep): boolean {
  const fromLane = unitLane(from.unitProcedure);
  const toLane = unitLane(to.unitProcedure);
  if (fromLane === toLane) return from.ordinal < to.ordinal;
  return laneTail(steps, fromLane)?.id === from.id && laneHead(steps, toLane)?.id === to.id;
}

/** 没有后继边的工步：可以被"汇合到当前工步"接进来的候选。 */
export function terminalSteps(steps: TopoStep[], edges: TopoEdge[], excludeId: string): TopoStep[] {
  const outgoing = new Set(edges.map((e) => e.fromStepId));
  return steps.filter((s) => s.id !== excludeId && !outgoing.has(s.id));
}

/** 缺 Unit Procedure / Operation 的工步；返回要提示的文案，全部合规时返回 null。 */
export function missingIsa88Name(steps: TopoStep[]): string | null {
  for (const s of steps) {
    if (!s.unitProcedure?.trim()) return t("工步 {0} 必须填写单元规程", s.code);
    if (!s.operation?.trim()) return t("工步 {0} 必须填写操作", s.code);
  }
  return null;
}

/**
 * 非法 PLC 程序号：7 / 8 是上位机工步（质检、人工确认）占用的号，工艺相不能占用；
 * 范围外同样拒绝。返回要提示的文案，全部合规时返回 null。
 */
export function invalidPlcProgram(steps: TopoStep[]): string | null {
  for (const s of steps) {
    if (isHostStepType(s.type)) continue;
    const id = resolvePlcProgram(s.type, s.plcProgramId);
    if (id === 7 || id === 8 || id < 1 || id > 99)
      return t("工步 {0} 的 PLC 程序号 {1} 无效（写 PLC 用 1–6 或 9–99）", s.code, id);
  }
  return null;
}

/** 新工步的默认编码：按现有数量续号，不复用被删除的号（履历里工步码要能对上）。 */
export function nextStepCode(count: number): string {
  return `S${(count + 1) * 10}`;
}
