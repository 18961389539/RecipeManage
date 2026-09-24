import { computed, ref, watch } from "vue";
import type { EquipmentClassDto, RecipeVersionDto, StepDto } from "../api/types";
import { classByUnit, isHostStepType, resolvePlcProgram } from "./plcProgram";
import { templateProgram } from "./phaseTemplate";
import { collectLanes, unitLane } from "../procedureLayout";

/**
 * ISA-88 泳道与"单元 → 设备类"的绑定关系。
 *
 * 为什么单独成一个 composable：一个单元（泳道）对应一台设备类，类决定了能选哪些相模板、
 * 程序号合法集合，以及上位机工步（质检/等待/人工确认）该继承哪一类的设备。
 * 这套推导以前散在设计器里 8 个函数 + 3 个 watch 中，改一处就要在四个入口（切车道、
 * 换设备类下拉、加工步、并行单元）里互相覆盖，是全仓最集中的一块工艺规则。
 *
 * 规则：
 * - `laneClassOverride` 是"人已经表态过"的映射，优先于推断；
 * - 推断取"能容纳本单元全部程序号的最窄设备类"（模板数最少的那个）；
 * - 上位机工步自己没有程序号，从汇入边所属单元继承。
 */
export function useProcedureLanes(options: {
  working: { readonly value: RecipeVersionDto | null };
  selected: { readonly value: StepDto | null };
  phaseClasses: { readonly value: EquipmentClassDto[] };
  editable: { readonly value: boolean };
}) {
  const { working, selected, phaseClasses, editable } = options;

  const paletteClassId = ref("");
  const laneClassOverride = ref<Record<string, string>>({});

  const paletteClass = computed(() =>
    phaseClasses.value.find((c) => c.id === paletteClassId.value) ?? null);
  const laneNames = computed(() => collectLanes(working.value?.steps ?? []));
  const currentLane = computed(() => unitLane(selected.value?.unitProcedure));

  function classIdOf(code?: string | null) {
    if (!code) return undefined;
    return phaseClasses.value.find((c) => c.code.toUpperCase() === code.toUpperCase())?.id;
  }

  function codeOf(classId?: string | null) {
    if (!classId) return undefined;
    return phaseClasses.value.find((c) => c.id === classId)?.code;
  }

  /** 车道上显示的类码：人表态过的优先，否则用工步上已写的设备类兜底。 */
  function laneClassCode(lane: string): string {
    const fromOverride = codeOf(laneClassOverride.value[lane]);
    if (fromOverride) return fromOverride;
    return (working.value?.steps ?? []).find((s) => unitLane(s.unitProcedure) === lane && s.equipmentClassCode)
      ?.equipmentClassCode ?? "";
  }

  function rememberLaneClass(lane: string, classId: string) {
    if (!lane || !classId) return;
    laneClassOverride.value = { ...laneClassOverride.value, [lane]: classId };
  }

  /** 把类码盖到该单元的所有工步上（含上位机工步，它们靠这个值通过相能力校验）。 */
  function stampLaneClass(lane: string, classId: string) {
    const code = codeOf(classId);
    if (!code || !working.value) return;
    for (const s of working.value.steps)
      if (unitLane(s.unitProcedure) === lane) s.equipmentClassCode = code;
    stampEmptyHostClasses();
  }

  function stampEmptyHostClasses() {
    if (!working.value || !editable.value) return;
    const map = classByUnit(working.value.steps, working.value.edges);
    for (const s of working.value.steps) {
      if (!isHostStepType(s.type) || s.equipmentClassCode) continue;
      const inherited = map.get(unitLane(s.unitProcedure));
      if (inherited) s.equipmentClassCode = inherited;
    }
  }

  function inferClassIdForLane(lane: string): string | null {
    const steps = (working.value?.steps ?? []).filter(
      (s) => unitLane(s.unitProcedure) === lane && !isHostStepType(s.type)
    );
    if (!steps.length || !phaseClasses.value.length) return null;
    const programs = new Set(steps.map((s) => resolvePlcProgram(s.type, s.plcProgramId)));
    const matches = phaseClasses.value.filter((c) => {
      const allowed = new Set(c.templates.map(templateProgram));
      return [...programs].every((p) => allowed.has(p));
    });
    if (!matches.length) return null;
    return [...matches].sort((a, b) => a.templates.length - b.templates.length)[0].id;
  }

  /** 换车道时把下拉指到该车道自己的类上：人表态过的用表态值，否则用推断值。 */
  function applyLaneClass() {
    const override = laneClassOverride.value[currentLane.value];
    if (override && phaseClasses.value.some((c) => c.id === override)) {
      paletteClassId.value = override;
      return;
    }
    const inferred = inferClassIdForLane(currentLane.value);
    if (inferred) paletteClassId.value = inferred;
  }

  /** 换版本/首次加载：按已存的工步设备类重建整张映射，避免"切版本后类还留着"。 */
  function hydrateLaneClasses() {
    const next: Record<string, string> = {};
    const inferred = classByUnit(working.value?.steps ?? [], working.value?.edges ?? []);
    for (const lane of laneNames.value) {
      const id = classIdOf(inferred.get(lane));
      if (id) next[lane] = id;
    }
    laneClassOverride.value = next;
    applyLaneClass();
    if (!paletteClassId.value)
      paletteClassId.value = phaseClasses.value[0]?.id ?? "";
  }

  function selectLane(lane: string) {
    const step = (working.value?.steps ?? []).find((s) => unitLane(s.unitProcedure) === lane);
    return step?.id ?? null;
  }

  watch(currentLane, applyLaneClass);
  watch(() => working.value?.id, hydrateLaneClasses);

  return {
    paletteClassId, paletteClass, laneNames, currentLane, laneClassOverride,
    laneClassCode, rememberLaneClass, stampLaneClass, stampEmptyHostClasses,
    inferClassIdForLane, applyLaneClass, hydrateLaneClasses, selectLane, classIdOf, codeOf
  };
}
