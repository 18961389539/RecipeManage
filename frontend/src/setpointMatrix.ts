export interface MatrixParameter {
  name: string;
  engineeringUnit: string;
  setpoint: number;
  min?: number | null;
  max?: number | null;
  archiveAsQuality?: boolean;
  slotIndex?: number;
}

export interface MatrixStep<T extends MatrixParameter = MatrixParameter> {
  id: string;
  code: string;
  name: string;
  ordinal: number;
  parameters: T[];
}

export interface MatrixColumn {
  id: string;
  label: string;
  title: string;
}

export interface MatrixRow<T extends MatrixParameter = MatrixParameter> {
  key: string;
  name: string;
  unit: string;
  cells: Record<string, T | null>;
}

export interface SetpointMatrixModel<T extends MatrixParameter = MatrixParameter> {
  columns: MatrixColumn[];
  rows: MatrixRow<T>[];
}

export function buildSetpointMatrix<T extends MatrixParameter>(
  steps: MatrixStep<T>[]
): SetpointMatrixModel<T> {
  const ordered = [...steps].sort((a, b) => a.ordinal - b.ordinal);
  const names: { key: string; name: string; unit: string }[] = [];
  const seen = new Set<string>();
  for (const step of ordered) {
    for (const p of step.parameters) {
      const key = `${p.name}|${p.engineeringUnit}`;
      if (seen.has(key)) continue;
      seen.add(key);
      names.push({ key, name: p.name, unit: p.engineeringUnit });
    }
  }
  return {
    columns: ordered.map((s) => ({ id: s.id, label: s.code, title: s.name })),
    rows: names.map((n) => ({
      key: n.key,
      name: n.name,
      unit: n.unit,
      cells: Object.fromEntries(
        ordered.map((s) => [
          s.id,
          s.parameters.find((p) => p.name === n.name && p.engineeringUnit === n.unit) ?? null
        ])
      ) as Record<string, T | null>
    }))
  };
}

export function snapshotToMatrixSteps<T extends MatrixParameter>(
  steps: Array<{
    stepId: string;
    code: string;
    name: string;
    ordinal: number;
    parameters: T[];
  }>
): MatrixStep<T>[] {
  return steps.map((s) => ({
    id: s.stepId,
    code: s.code,
    name: s.name,
    ordinal: s.ordinal,
    parameters: s.parameters
  }));
}

export function formatSetpointSpec(param: MatrixParameter | null | undefined): string {
  if (!param) return "—";
  if (param.min == null && param.max == null) return String(param.setpoint);
  return `${param.setpoint}（${param.min ?? "—"}~${param.max ?? "—"}）`;
}

export interface MeasuredCell {
  value: number;
  oos: boolean;
}

/** stepId → 参数名 → 实测值。原先缺失此导出，导致 vue-tsc 报 TS2305/TS2304。 */
export type MeasuredByStep = Record<string, Record<string, MeasuredCell>>;

/**
 * 一条归档质检读数。矩阵、监控页质检表、电子批记录三处都从这里投影，
 * 判定口径（哪些测点算数、什么叫超差）只在本文件里决定一次。
 */
export interface QualityReading {
  stepId: string;
  stepCode: string;
  tag: string;
  /** 非数值读数为 null，原文放 raw —— 之前 eBR 直接 Number(value).toFixed(2) 打出 "NaN" */
  value: number | null;
  raw: string | null;
  min: number | null;
  max: number | null;
  unit: string;
  /**
   * 是否计入质量归档判定。PLC 只读信号、显式 archiveAsQuality=false 的参数、
   * 以及取不到数值的行都不计入：矩阵不显示它们，质检表也一律不判超差。
   * 此前矩阵会隐藏这些点而两张表格照样按规格判超差，同一测点两处结论互相矛盾。
   */
  archived: boolean;
  oos: boolean;
}

interface QualitySource {
  stepId: string;
  stepCode?: string;
  qualityJson?: string | null;
  outcome?: string | null;
}

export function qualityReadings(
  steps: Array<{ id?: string; stepId?: string; parameters: MatrixParameter[] }>,
  executions: QualitySource[]
): QualityReading[] {
  const byId = new Map(steps.map((s) => [s.stepId ?? s.id ?? "", s]));
  const rows: QualityReading[] = [];
  for (const exec of executions) {
    if (exec.outcome === "Skipped" || exec.outcome === "Faulted") continue;
    const text = exec.qualityJson?.trim();
    if (!text?.startsWith("{")) continue;
    let parsed: Record<string, unknown>;
    try {
      parsed = JSON.parse(text) as Record<string, unknown>;
    } catch {
      rows.push({
        stepId: exec.stepId, stepCode: exec.stepCode ?? "", tag: "raw", value: null,
        raw: exec.qualityJson ?? "", min: null, max: null, unit: "", archived: false, oos: false
      });
      continue;
    }
    const snap = byId.get(exec.stepId);
    for (const [tag, value] of Object.entries(parsed)) {
      const numeric = typeof value === "number" ? value : Number(value);
      const finite = Number.isFinite(numeric);
      const param = snap?.parameters.find((p) => p.name === tag);
      const archived = finite && !tag.startsWith("PLC:") && param?.archiveAsQuality !== false;
      const oos =
        archived &&
        param != null &&
        ((param.min != null && numeric < param.min) || (param.max != null && numeric > param.max));
      rows.push({
        stepId: exec.stepId,
        stepCode: exec.stepCode ?? "",
        tag,
        value: finite ? numeric : null,
        raw: finite ? null : String(value),
        min: param?.min ?? null,
        max: param?.max ?? null,
        unit: param?.engineeringUnit ?? "",
        archived,
        oos
      });
    }
  }
  return rows;
}

/** 规格列文案。三处表格共用，避免同一测点在监控页写 750~850、在 eBR 写 750~850 ℃。 */
export function formatReadingSpec(reading: QualityReading): string {
  if (reading.oos) return "超差";
  if (reading.min != null || reading.max != null)
    return `${reading.min ?? "—"}~${reading.max ?? "—"} ${reading.unit}`.trim();
  return reading.archived ? "—" : "参考";
}

export function formatReadingValue(reading: QualityReading): string {
  return reading.value !== null ? reading.value.toFixed(2) : (reading.raw ?? "—");
}

export function measuredFromExecutions(
  steps: Array<{ id?: string; stepId?: string; parameters: MatrixParameter[] }>,
  executions: Array<{ stepId: string; qualityJson?: string | null; outcome?: string | null }>
): MeasuredByStep {
  const out: MeasuredByStep = {};
  for (const reading of qualityReadings(steps, executions)) {
    if (!reading.archived || reading.value === null) continue;
    (out[reading.stepId] ??= {})[reading.tag] = { value: reading.value, oos: reading.oos };
  }
  return out;
}

/** Keys `${stepId}|${name}|${unit}` for cells whose setpoint/spec changed vs another version. */
export function changedMatrixKeys(
  steps: MatrixStep[],
  diff: { changes?: Array<{ path: string }> } | null | undefined
): Set<string> {
  const keys = new Set<string>();
  if (!diff?.changes?.length) return keys;
  const byCode = new Map(steps.map((s) => [s.code, s]));
  const re = /^([^.]+)\.parameters\[(\d+)\]/;
  for (const change of diff.changes) {
    const m = re.exec(change.path);
    if (!m) continue;
    const step = byCode.get(m[1]);
    if (!step) continue;
    const slot = Number(m[2]);
    const param = step.parameters.find((p) => p.slotIndex === slot) ?? step.parameters[slot];
    if (!param) continue;
    keys.add(`${step.id}|${param.name}|${param.engineeringUnit}`);
  }
  return keys;
}
