/** ISA-88 单元规程泳道排布。设计态保存坐标；执行快照不含 canvas，以免破坏完整性哈希。 */

export const DEFAULT_UNIT_LANE = "UP-01 热处理单元";
export const LAYOUT_X0 = 80;
export const LAYOUT_Y0 = 48;
export const LAYOUT_DX = 210;
export const LAYOUT_DY = 170;

export interface ProcedureLayoutStep {
  id: string;
  unitProcedure?: string | null;
  ordinal: number;
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

/** 设计态「ISA-88 泳道排布」：按单元内 ordinal 从左到右。 */
export function layoutByLaneOrdinal(steps: ProcedureLayoutStep[]): Record<string, { x: number; y: number }> {
  const lanes = collectLanes(steps);
  const indexInLane: Record<string, number> = {};
  const pos: Record<string, { x: number; y: number }> = {};
  for (const step of [...steps].sort((a, b) => a.ordinal - b.ordinal)) {
    const lane = unitLane(step.unitProcedure);
    const i = indexInLane[lane] ?? 0;
    pos[step.id] = {
      x: LAYOUT_X0 + i * LAYOUT_DX,
      y: LAYOUT_Y0 + Math.max(0, lanes.indexOf(lane)) * LAYOUT_DY
    };
    indexInLane[lane] = i + 1;
  }
  return pos;
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
