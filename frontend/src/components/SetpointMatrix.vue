<template>
  <el-table
    v-if="matrix.rows.length"
    :data="matrix.rows"
    border
    size="small"
    :max-height="maxHeight"
  >
    <el-table-column prop="name" label="参数" width="140" fixed />
    <el-table-column prop="unit" label="单位" width="64" />
    <el-table-column v-for="col in matrix.columns" :key="col.id" :label="col.label" min-width="128">
      <template #header>
        <button
          class="matrix-col"
          :class="{ on: col.id === selectedId, current: col.id === currentStepId }"
          type="button"
          :title="col.title"
          @click="emit('select', col.id)"
        >
          {{ col.label }}
        </button>
      </template>
      <template #default="{ row }">
        <template v-if="row.cells[col.id]">
          <el-input-number
            v-if="!readonly"
            v-model="row.cells[col.id]!.setpoint"
            :controls="false"
            @focus="emit('select', col.id)"
          />
          <button
            v-else
            class="cell"
            :class="{ on: col.id === selectedId, current: col.id === currentStepId, oos: row.actuals[col.id]?.oos, diff: isChanged(col.id, row) }"
            type="button"
            @click="emit('select', col.id)"
          >
            <span class="sp">{{ row.cells[col.id]!.setpoint }}</span>
            <span v-if="row.cells[col.id]!.min != null || row.cells[col.id]!.max != null" class="spec">
              {{ row.cells[col.id]!.min ?? "—" }}~{{ row.cells[col.id]!.max ?? "—" }}
            </span>
            <span v-if="row.actuals[col.id]" class="meas" :class="{ bad: row.actuals[col.id]!.oos }">
              实测 {{ formatActual(row.actuals[col.id]!.value) }}
              {{ row.actuals[col.id]!.oos ? "超差" : "合格" }}
            </span>
          </button>
        </template>
        <span v-else class="muted">—</span>
      </template>
    </el-table-column>
  </el-table>
  <el-empty v-else description="尚无工步参数可组成 Setpoints 矩阵" />
</template>

<script setup lang="ts">
import { computed } from "vue";
import {
  buildSetpointMatrix,
  type MatrixStep,
  type MeasuredByStep,
  type MeasuredCell
} from "../setpointMatrix";

const props = withDefaults(defineProps<{
  steps: MatrixStep[];
  selectedId?: string | null;
  currentStepId?: string | null;
  readonly?: boolean;
  maxHeight?: number;
  measured?: MeasuredByStep;
  changedKeys?: Set<string>;
}>(), {
  selectedId: null,
  currentStepId: null,
  readonly: true,
  maxHeight: 340,
  measured: () => ({}),
  changedKeys: () => new Set<string>()
});

const emit = defineEmits<{ select: [stepId: string] }>();

const matrix = computed(() => {
  const base = buildSetpointMatrix(props.steps);
  return {
    columns: base.columns,
    rows: base.rows.map((row) => ({
      ...row,
      actuals: Object.fromEntries(
        base.columns.map((col) => {
          const param = row.cells[col.id];
          const archived = param && param.archiveAsQuality !== false;
          const hit = param && archived
            ? props.measured?.[col.id]?.[param.name] ?? null
            : null;
          return [col.id, hit as MeasuredCell | null];
        })
      ) as Record<string, MeasuredCell | null>
    }))
  };
});

function formatActual(value: number) {
  return Number.isInteger(value) ? String(value) : value.toFixed(2);
}

function isChanged(stepId: string, row: { key: string }) {
  return props.changedKeys.has(`${stepId}|${row.key}`);
}
</script>

<style scoped>
.muted { color: var(--muted); font-size: 12px; }
.matrix-col {
  border: 0; background: transparent; color: inherit; cursor: pointer; font: inherit;
  padding: 0;
}
/* 表头与单元格都是「点它即联动选中该工步」的入口，此前选中只把文字变蓝，
   在一整片同色表头里几乎看不出来；补下划线 + 悬停反馈。 */
.matrix-col:hover, .cell:hover { color: var(--accent-bright); }
.matrix-col { text-align: left; }
.matrix-col.on { color: var(--accent-bright); border-bottom: 2px solid var(--accent); padding-bottom: 2px; }
.cell.on { color: var(--accent-bright); box-shadow: inset 2px 0 0 var(--accent); }
.matrix-col.current { color: var(--warn); border-bottom: 2px solid var(--warn); padding-bottom: 2px; }
.cell.current { color: var(--warn); box-shadow: inset 2px 0 0 var(--warn); }
.cell {
  display: flex; flex-direction: column; align-items: flex-start; gap: 2px;
  width: 100%; border: 0; background: transparent; color: inherit; cursor: pointer;
  font: inherit; text-align: left; padding: 0; line-height: 1.5;
}
.sp { font-variant-numeric: tabular-nums; font-weight: 600; }
.spec { color: var(--muted); font-size: 11px; font-variant-numeric: tabular-nums; }
/* 实测值前加状态点：归档表里合格/超差靠文字颜色区分，密集矩阵中扫不出异常行 */
.meas { display: inline-flex; align-items: center; gap: 5px; color: var(--ok); font-size: 11px; }
.meas::before { content: ""; width: 6px; height: 6px; border-radius: 50%; background: currentColor; }
.meas.bad, .cell.oos .meas { color: var(--err); }
.cell.diff .sp { color: var(--warn); }
</style>
