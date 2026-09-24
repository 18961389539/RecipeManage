<template>
  <div class="step-node" :class="[data.type, outcomeClass, data.change, { current: data.current, picked: data.picked }]">
    <Handle type="target" :position="Position.Left" />
    <div class="code">{{ data.code }}</div>
    <div class="name">{{ data.name }}</div>
    <div v-if="data.unitProcedure" class="op">{{ data.unitProcedure }}</div>
    <div v-if="data.operation" class="op">{{ data.operation }}</div>
    <div v-if="data.plcProgramId" class="op">{{ $t("程序 {0}", [data.plcProgramId]) }}</div>
    <div v-if="changeLabel" class="outcome">{{ changeLabel }}</div>
    <div v-if="outcomeLabel" class="outcome">{{ outcomeLabel }}</div>
    <Handle type="source" :position="Position.Right" />
  </div>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { Handle, Position } from "@vue-flow/core";
import { stepOutcomeLabel } from "../utils/labels";

const props = defineProps<{
  data: {
    code: string;
    name: string;
    type: string;
    operation?: string | null;
    unitProcedure?: string | null;
    plcProgramId?: number | null;
    outcome?: string | null;
    current?: boolean;
    picked?: boolean;
    change?: "added" | "changed" | null;
  };
}>();

const outcomeClass = computed(() => `out-${props.data.outcome || "Pending"}`);
const changeLabel = computed(() => {
  if (props.data.change === "added") return "新增";
  if (props.data.change === "changed") return "有改";
  return "";
});
const outcomeLabel = computed(() => {
  const o = props.data.outcome;
  // 未开工步不写"待执行"：全景视图里每个未到达的节点都挂同样的四个字，纯噪声。
  if (!o || o === "Pending") return "";
  return stepOutcomeLabel(o);
});
</script>

<style scoped>
.step-node {
  /* 固定宽度：此前只有 min-width，长工步名会把节点撑成不规则宽度，
     同一泳道里参差不齐，横向对齐关系读不出来 */
  width: 172px;
  padding: var(--space-2) var(--space-3);
  border-radius: 8px;
  border: 1px solid var(--accent);
  background: var(--raised);
  color: var(--text);
  font-size: 12px;
  line-height: 1.45;
  cursor: pointer;
  transition: background-color 0.15s ease, box-shadow 0.15s ease;
}
.step-node:hover { background: var(--hover); }
.step-node .code { font-size: 13px; font-weight: 700; letter-spacing: 0.4px; }
.step-node .name { color: var(--text-body); margin-top: 2px; }
.step-node .op { color: var(--muted); margin-top: 2px; font-size: 11px; }
.step-node .outcome { margin-top: 5px; font-size: 11px; font-weight: 600; color: var(--text-body); }
.step-node.Heat, .step-node.Hold { border-color: var(--warn); }
.step-node.Cool { border-color: var(--cool); }
.step-node.QualityCheck { border-color: var(--ok); }
.step-node.Pressure, .step-node.Mix { border-color: var(--violet); }
.step-node.Transfer, .step-node.ManualConfirm, .step-node.Wait { border-color: var(--muted); }
.out-Completed { border-color: var(--ok) !important; box-shadow: 0 0 0 1px var(--ok) inset; }
.out-Running { border-color: var(--accent) !important; }
.out-AwaitingConfirm, .out-Held { border-color: var(--warn) !important; }
.out-Faulted { border-color: var(--err) !important; }
.out-Skipped { border-color: var(--muted) !important; opacity: 0.72; }
/* 状态文字跟着状态色走：此前只有边框变色，完成/故障两行文字同色，
   缩小到全景视图时只能靠边框颜色判断，色觉不便时更难 */
.out-Completed .outcome { color: var(--ok); }
.out-Running .outcome { color: var(--accent-bright); }
.out-AwaitingConfirm .outcome, .out-Held .outcome { color: var(--warn); }
.out-Faulted .outcome { color: var(--err); }
.out-Skipped .outcome { color: var(--muted); }
/* 当前工步：环 + 辉光写在同一条 box-shadow 里，
   分两条规则会互相覆盖（此前 .current 把 .out-Running 的辉光顶掉了） */
.current { box-shadow: 0 0 0 2px var(--accent), 0 0 12px rgb(var(--accent-rgb) / 0.35); }
.picked { outline: 2px dashed var(--accent-bright); outline-offset: 2px; }
.added { box-shadow: 0 0 0 2px var(--ok); }
.changed { box-shadow: 0 0 0 2px var(--warn); }
</style>
