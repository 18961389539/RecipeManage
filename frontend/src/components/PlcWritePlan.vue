<template>
  <el-table
    class="write-plan"
    :data="items"
    size="small"
    border
    :max-height="maxHeight"
    :row-class-name="rowClass"
  >
    <el-table-column prop="stepCode" :label="$t('工步')" width="70" fixed />
    <el-table-column prop="stepName" :label="$t('名称')" min-width="120" />
    <el-table-column width="88">
      <template #header><HelpTip term="Step_ID" /></template>
      <template #default="{ row }">{{ row.writeToPlc ? row.plcStepId : "—" }}</template>
    </el-table-column>
    <el-table-column :label="$t('写参')">
      <template #default="{ row }">{{ formatParams(row) }}</template>
    </el-table-column>
    <el-table-column :label="$t('策略')" min-width="200">
      <template #default="{ row }">
        <span :class="{ host: !row.writeToPlc }">{{ $t(row.policy) }}</span>
      </template>
    </el-table-column>
  </el-table>
</template>

<script setup lang="ts">
import { t } from "../i18n";
import type { PlcWritePlanDto } from "../api/types";
import HelpTip from "./HelpTip.vue";

const props = withDefaults(defineProps<{
  items: PlcWritePlanDto[];
  currentStepId?: string | null;
  maxHeight?: number;
}>(), {
  currentStepId: null,
  maxHeight: 240
});

function formatParams(row: PlcWritePlanDto) {
  if (!row.writeToPlc)
    return t("禁止写 PLC");
  const slots = (row.parameters ?? [])
    .map((value, index) => ({ index, value }))
    .filter((p) => p.value !== 0)
    .map((p) => `Param[${p.index}]=${Number(p.value).toFixed(Number.isInteger(p.value) ? 0 : 2)}`);
  return [`Step_Type=${row.plcStepType}`, ...slots].join(" ");
}

function rowClass({ row }: { row: PlcWritePlanDto }) {
  const bits = [];
  if (row.stepId === props.currentStepId) bits.push("current-write");
  if (!row.writeToPlc) bits.push("host-step");
  return bits.join(" ");
}
</script>

<style scoped>
.host { color: var(--warn); }
</style>
<style>
.write-plan .current-write td { background: var(--tint) !important; }
.write-plan .host-step td { color: var(--text-body); }
</style>
