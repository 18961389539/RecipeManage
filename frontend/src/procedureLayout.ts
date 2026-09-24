/** ISA-88 单元规程泳道排布。设计器与监控共用 layoutProcedure；执行快照不含 canvas，以免破坏完整性哈希。 */

/** 与后端 Isa88.DefaultUnitProcedure 同值：单元规程为空时的兜底泳道名。 */
export const DEFAULT_UNIT_LANE = "UP-01";
export const LAYOUT_X0 = 80;
export const LAYOUT_Y0 = 48;
export const LAYOUT_DX = 210;
export const LAYOUT_DY = 170;

export interface ProcedureLayoutStep {
  id: string;
  unitProcedure?: string | null;
  ordinal: number;
}

/**
 * 画布真正需要的节点形状。原先声明在 ProcedureFlow.vue 内部，
 * 但快照投影（useBatchSnapshotView）与电子批记录的归档区都要按同一形状传参，
 * 声明在组件里就导不出去，只能各处再抄一份字段表——抄一份就会漂一份。
 */
export interface ProcedureFlowStep extends ProcedureLayoutStep {
  code: string;
  name: string;
  type: string;
  operation?: string | null;
  plcProgramId?: number | null;
}

export interface ProcedureLayoutEdge {
  from: string;
  to: string;
}

export function unitLane(unit?: string | null): string {
  const trimmed = unit?.trim();
  return trimmed ? trimmed : DEFAULT_UNIT_LANE;
}

export function collectLanes(steps: ProcedureLayoutStep[]): string[] {
  const lanes: string[] = [];
  for (const step of [...steps].sort((a, b) => a.ordinal - b.ordinal)) {
    const lane = unitLane(step.unitProcedure);
    if (!lanes.includes(lane)) lanes.push(lane);
  }
  return lanes;
}

/** 无拓扑边时的泳道排布：与执行态同一套算法，避免设计器/监控两套坐标。 */
export function layoutByLaneOrdinal(steps: ProcedureLayoutStep[]): Record<string, { x: number; y: number }> {
  return layoutProcedure(steps, []);
}

/** 执行态：按冻结边做最长路径分层；无边时回退到泳道 ordinal。 */
export function layoutProcedure(
  steps: ProcedureLayoutStep[],
  edges: ProcedureLayoutEdge[]
): Record<string, { x: number; y: number }> {
  const ids = new Set(steps.map((s) => s.id));
  const usable = edges.filter((e) => ids.has(e.from) && ids.has(e.to));
  const rank = longestPathRanks(steps, usable);
  const lanes = collectLanes(steps);
  const stack = new Map<string, number>();
  const pos: Record<string, { x: number; y: number }> = {};
  for (const step of [...steps].sort((a, b) => a.ordinal - b.ordinal)) {
    const lane = unitLane(step.unitProcedure);
    const r = rank[step.id] ?? step.ordinal;
    const key = `${lane}|${r}`;
    const k = stack.get(key) ?? 0;
    stack.set(key, k + 1);
    pos[step.id] = {
      x: LAYOUT_X0 + r * LAYOUT_DX,
      y: LAYOUT_Y0 + Math.max(0, lanes.indexOf(lane)) * LAYOUT_DY + k * 36
    };
  }
  return pos;
}

export function sequentialFallbackEdges(steps: ProcedureLayoutStep[]): ProcedureLayoutEdge[] {
  const ordered = [...steps].sort((a, b) => a.ordinal - b.ordinal);
  const edges: ProcedureLayoutEdge[] = [];
  for (let i = 1; i < ordered.length; i++)
    edges.push({ from: ordered[i - 1].id, to: ordered[i].id });
  return edges;
}

function longestPathRanks(steps: ProcedureLayoutStep[], edges: ProcedureLayoutEdge[]): Record<string, number> {
  const rank: Record<string, number> = {};
  for (const step of steps) rank[step.id] = 0;
  if (!edges.length) {
    const indexInLane: Record<string, number> = {};
    for (const step of [...steps].sort((a, b) => a.ordinal - b.ordinal)) {
      const lane = unitLane(step.unitProcedure);
      const i = indexInLane[lane] ?? 0;
      rank[step.id] = i;
      indexInLane[lane] = i + 1;
    }
    return rank;
  }
  for (let n = 0; n < steps.length; n++) {
    let changed = false;
    for (const edge of edges) {
      const next = (rank[edge.from] ?? 0) + 1;
      if (next > (rank[edge.to] ?? 0)) {
        rank[edge.to] = next;
        changed = true;
      }
    }
    if (!changed) break;
  }
  return rank;
}
