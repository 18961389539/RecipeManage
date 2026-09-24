import type { EquipmentClassDto, StepType } from "../api/types";
import { unitLane } from "../procedureLayout";

export const hostStepTypes: StepType[] = ["Wait", "QualityCheck", "ManualConfirm"];

const defaultPlcProgramByType: Record<StepType, number> = {
  Wait: 0,
  Heat: 1,
  Hold: 2,
  Cool: 3,
  Mix: 4,
  Pressure: 5,
  Transfer: 6,
  QualityCheck: 7,
  ManualConfirm: 8
};

export type ExecutionKind = "WritePlc" | "Wait" | "QualityCheck" | "ManualConfirm";

export function executionKind(type: StepType): ExecutionKind {
  if (type === "Wait") return "Wait";
  if (type === "QualityCheck") return "QualityCheck";
  if (type === "ManualConfirm") return "ManualConfirm";
  return "WritePlc";
}

export function isHostStepType(type: StepType) {
  return executionKind(type) !== "WritePlc";
}

export function resolvePlcProgram(type: StepType, plcProgramId?: number | null) {
  return plcProgramId ?? defaultPlcProgramByType[type];
}

export function allowedProgramsByClass(classes: EquipmentClassDto[]): Record<string, number[]> {
  const map: Record<string, number[]> = {};
  for (const cls of classes) {
    map[cls.code.toUpperCase()] = cls.templates.map((t) => resolvePlcProgram(t.stepType, t.plcProgramId));
  }
  return map;
}

export function programsByUnit(
  steps: { type: StepType; plcProgramId?: number | null; unitProcedure?: string | null }[]
): Map<string, number[]> {
  const map = new Map<string, Set<number>>();
  for (const step of steps) {
    if (isHostStepType(step.type)) continue;
    const lane = unitLane(step.unitProcedure);
    const set = map.get(lane) ?? new Set<number>();
    set.add(resolvePlcProgram(step.type, step.plcProgramId));
    map.set(lane, set);
  }
  return new Map([...map].map(([lane, set]) => [lane, [...set]]));
}

export function classByUnit(
  steps: { id?: string; unitProcedure?: string | null; equipmentClassCode?: string | null; type?: StepType }[],
  edges?: { fromStepId: string; toStepId: string }[]
): Map<string, string> {
  const map = new Map<string, string>();
  for (const step of steps) {
    const code = step.equipmentClassCode?.trim().toUpperCase();
    if (!code) continue;
    const lane = unitLane(step.unitProcedure);
    if (!map.has(lane)) map.set(lane, code);
  }
  if (!edges?.length) return map;
  const byId = new Map(steps.filter((s) => s.id).map((s) => [s.id as string, s]));
  const writeLanes = new Set(
    steps.filter((s) => s.type && !isHostStepType(s.type)).map((s) => unitLane(s.unitProcedure))
  );
  for (const lane of new Set(steps.map((s) => unitLane(s.unitProcedure)))) {
    if (map.has(lane) || writeLanes.has(lane)) continue;
    const incoming = new Set<string>();
    for (const edge of edges) {
      const from = byId.get(edge.fromStepId);
      const to = byId.get(edge.toStepId);
      if (!from || !to) continue;
      if (unitLane(to.unitProcedure) !== lane) continue;
      const fromLane = unitLane(from.unitProcedure);
      if (fromLane === lane) continue;
      const src = map.get(fromLane);
      if (src) incoming.add(src);
    }
    if (incoming.size === 1)
      map.set(lane, [...incoming][0]);
  }
  return map;
}

export function classConflicts(declared?: string | null, actual?: string | null) {
  const want = declared?.trim().toUpperCase();
  const have = actual?.trim().toUpperCase();
  if (!want || !have || want === "GENERIC" || have === "GENERIC") return false;
  return want !== have;
}

/** 与 EquipmentClassRules.EnsureCompatible 一致：未分类或未知类放行。 */
export function programsRejectedByClass(
  equipmentClassCode: string | null | undefined,
  programs: number[],
  allowedByClass: Record<string, number[]>
): number[] {
  if (!programs.length) return [];
  const code = equipmentClassCode?.trim().toUpperCase();
  if (!code) return [];
  const allowed = allowedByClass[code];
  if (!allowed) return [];
  const ok = new Set(allowed);
  return programs.filter((p) => !ok.has(p));
}
