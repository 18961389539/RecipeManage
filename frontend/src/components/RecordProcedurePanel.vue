<template>
  <ProcedureFlow
    flow-id="batch-record-flow"
    :steps="flowSteps"
    :edges="edges"
    :outcomes="outcomes"
    :height="260"
  />
  <el-table :data="steps" size="small" border>
    <el-table-column prop="code" :label="$t('工步编码')" width="90" fixed />
    <el-table-column prop="name" :label="$t('工步')" />
    <el-table-column min-width="140">
      <template #header><HelpTip term="Unit Procedure">{{ $t("单元规程") }}</HelpTip></template>
      <template #default="{ row }">{{ row.unitProcedure }}</template>
    </el-table-column>
    <el-table-column min-width="140">
      <template #header><HelpTip term="Operation">{{ $t("操作") }}</HelpTip></template>
      <template #default="{ row }">{{ row.operation }}</template>
    </el-table-column>
    <el-table-column :label="$t('类型')" width="110">
      <template #default="{ row }">{{ phaseTypeLabel(row) }}</template>
    </el-table-column>
    <el-table-column :label="$t('结果')" width="110">
      <template #default="{ row }">{{ stepOutcomeLabel(outcomes[row.stepId] ?? "Pending") }}</template>
    </el-table-column>
    <el-table-column :label="$t('设定值')">
      <template #default="{ row }">
        {{ row.parameters.map((p: SnapshotParameter) => `${p.name}=${p.setpoint}${p.engineeringUnit}`).join("；") }}
      </template>
    </el-table-column>
  </el-table>
  <h4>{{ $t("冻结设定矩阵（设定 / 归档实测）") }}</h4>
  <SetpointMatrix :steps="matrixSteps" :measured="measured" readonly :max-height="280" />
  <h4>{{ $t("PLC 写参计划（拓扑顺序，禁止盲写）") }}</h4>
  <PlcWritePlan :items="writePlan" />
</template>

<script setup lang="ts">
import type { PlcWritePlanDto, SnapshotEdge, SnapshotParameter, SnapshotStep } from "../api/types";
import type { MatrixStep, MeasuredByStep } from "../setpointMatrix";
import type { ProcedureFlowStep } from "../procedureLayout";
import { phaseTypeLabel, stepOutcomeLabel } from "../utils/labels";
import ProcedureFlow from "./ProcedureFlow.vue";
import SetpointMatrix from "./SetpointMatrix.vue";
import PlcWritePlan from "./PlcWritePlan.vue";
import HelpTip from "./HelpTip.vue";

/**
 * ISA-88 快照区：画布、工步表、冻结设定矩阵、PLC 写参计划。
 *
 * 四块读的是同一份冻结快照，投影全部由 useBatchSnapshotView 算好后传进来——
 * 监控页用同一个投影，两处若各算各的，同一批次在两个页面上就会给出不同的结论。
 * 工步结论取 outcomes 映射（与画布同源），不再单独 find，避免同一页两种口径。
 */
defineProps<{
  steps: SnapshotStep[];
  edges: SnapshotEdge[];
  flowSteps: ProcedureFlowStep[];
  outcomes: Record<string, string>;
  matrixSteps: MatrixStep[];
  measured: MeasuredByStep;
  writePlan: PlcWritePlanDto[];
}>();
</script>

<style scoped>
h4 { margin: var(--space-3) 0 var(--space-2); font-size: 14px; }
</style>
