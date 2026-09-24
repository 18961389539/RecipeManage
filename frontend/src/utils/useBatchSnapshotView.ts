import { computed, type Ref } from "vue";
import { t } from "../i18n";
import type { BatchDetailDto } from "../api/types";
import {
  formatReadingSpec, formatReadingValue, measuredFromExecutions, qualityReadings, snapshotToMatrixSteps
} from "../setpointMatrix";
import { snapshotIntegrityLabel } from "./integrity";
import { DEFAULT_UNIT_LANE } from "../procedureLayout";

/**
 * 快照的展示投影：冻结的控制配方 → 画布、设定矩阵、单元分组、归档质检、漂移表。
 *
 * 这一层只读 `batch.snapshot` / `stepExecutions`，不看握手相位也不管能不能操作，
 * 所以是本页唯一可以脱离 DOM 逐例断言的地方（见 useBatchSnapshotView.spec.ts）。
 */
type SnapshotSource = Pick<BatchDetailDto, "snapshot" | "stepExecutions" | "snapshotIntegrity">;

// 泛型而不是直接收 SnapshotSource：Ref 是可写的，`Ref<Detail|null>` 不能赋给
// `Ref<Source|null>`；电子批记录用的是另一个 DTO（BatchRecordDto），三字段同型。
export function useBatchSnapshotView<T extends SnapshotSource>(
  batch: Ref<T | null>,
  pickedStep: Ref<string | null>,
  equipmentIndex: Ref<Record<string, string>>
) {
  function outcome(stepId: string) {
    return batch.value?.stepExecutions.find((s) => s.stepId === stepId)?.outcome ?? "Pending";
  }

  function timelineType(stepId: string) {
    const o = outcome(stepId);
    if (o === "Running") return "primary";
    if (o === "AwaitingConfirm") return "warning";
    if (o === "Held") return "warning";
    if (o === "Completed") return "success";
    if (o === "Skipped") return "warning";
    if (o === "Faulted") return "danger";
    return "info";
  }

  const flowSteps = computed(() =>
    (batch.value?.snapshot.steps ?? []).map((s) => ({
      id: s.stepId,
      code: s.code,
      name: s.name,
      type: s.type,
      unitProcedure: s.unitProcedure,
      operation: s.operation,
      plcProgramId: s.plcProgramId,
      ordinal: s.ordinal
    }))
  );
  const flowEdges = computed(() => batch.value?.snapshot.edges ?? []);
  const flowOutcomes = computed(() =>
    Object.fromEntries((batch.value?.stepExecutions ?? []).map((e) => [e.stepId, e.outcome]))
  );
  const matrixSteps = computed(() => snapshotToMatrixSteps(batch.value?.snapshot.steps ?? []));
  const measured = computed(() =>
    measuredFromExecutions(matrixSteps.value, batch.value?.stepExecutions ?? [])
  );

  const unitGroups = computed(() => {
    const map = new Map<string, NonNullable<typeof batch.value>["snapshot"]["steps"]>();
    for (const s of batch.value?.snapshot.steps ?? []) {
      const name = s.unitProcedure?.trim() || DEFAULT_UNIT_LANE;
      const list = map.get(name);
      if (list) list.push(s);
      else map.set(name, [s]);
    }
    return [...map.entries()].map(([name, steps]) => ({ name, steps }));
  });

  const qualityRows = computed(() => {
    const execs = batch.value?.stepExecutions ?? [];
    const focused = pickedStep.value ? execs.filter((s) => s.stepId === pickedStep.value) : execs;
    return qualityReadings(matrixSteps.value, focused).map((r) => ({
      step: r.stepCode,
      tag: r.tag,
      value: formatReadingValue(r),
      spec: formatReadingSpec(r),
      oos: r.oos
    }));
  });

  const integrityLabel = computed(() => snapshotIntegrityLabel(batch.value?.snapshotIntegrity));

  const scaleLabel = computed(() => {
    const snap = batch.value?.snapshot;
    if (!snap) return "";
    const parts: string[] = [];
    if (snap.scaleFactor != null && snap.scaleFactor !== 1) parts.push(t("缩放 ×{0}", snap.scaleFactor));
    if (snap.lotNumber) parts.push(t("物料 {0}", snap.lotNumber));
    const bindings = snap.unitEquipment
      ? Object.entries(snap.unitEquipment).map(([unit, id]) => `${unit}→${equipmentIndex.value[id] ?? id.slice(0, 8)}`)
      : [];
    if (bindings.length) parts.push(bindings.join("，"));
    return parts.length ? ` · ${parts.join(" · ")}` : "";
  });

  function unitEquipmentLabel(unit: string) {
    const id = batch.value?.snapshot.unitEquipment?.[unit];
    if (!id) return "";
    const code = equipmentIndex.value[id];
    return code ? ` · ${code}` : "";
  }

  return {
    outcome, timelineType, flowSteps, flowEdges, flowOutcomes,
    matrixSteps, measured, unitGroups, qualityRows,
    integrityLabel, scaleLabel, unitEquipmentLabel
  };
}
