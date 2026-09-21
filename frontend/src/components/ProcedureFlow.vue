<template>
  <div class="procedure-flow">
    <div v-if="lanes.length" class="lanes">
      <span v-for="lane in lanes" :key="lane" class="lane-chip">{{ lane }}</span>
    </div>
    <div class="legend">
      <span><i class="lg pending" />待执行</span>
      <span><i class="lg run" />执行中</span>
      <span><i class="lg wait" />待确认/保持</span>
      <span><i class="lg ok" />完成</span>
      <span><i class="lg skip" />跳过</span>
      <span><i class="lg err" />故障</span>
      <span v-if="hasMarkers"><i class="lg added" />相对生效版新增</span>
      <span v-if="hasMarkers"><i class="lg changed" />参数有改</span>
    </div>
    <div class="canvas" :style="{ height: `${height}px` }">
      <!-- fit-view-on-init 会按内容自动缩放，工步很少时不设上限会把节点放到巨大，
           因此把 max-zoom 收到 1.15，让两三个工步也保持正常字号。 -->
      <VueFlow
        v-if="nodes.length"
        :id="flowId"
        :nodes="nodes"
        :edges="graphEdges"
        :node-types="nodeTypes"
        :nodes-draggable="false"
        :nodes-connectable="false"
        :elements-selectable="true"
        :min-zoom="0.35"
        :max-zoom="1.15"
        fit-view-on-init
        @node-click="onNodeClick"
      >
        <Background :gap="18" :size="1" />
        <Controls />
      </VueFlow>
      <div v-else class="empty">控制配方快照尚无工步</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, markRaw } from "vue";
import { VueFlow, type Edge, type Node, type NodeComponent, type NodeMouseEvent } from "@vue-flow/core";
import { Background } from "@vue-flow/background";
import { Controls } from "@vue-flow/controls";
import "@vue-flow/core/dist/style.css";
import "@vue-flow/core/dist/theme-default.css";
import "@vue-flow/controls/dist/style.css";
import StepFlowNode from "./StepFlowNode.vue";
import {
  collectLanes,
  layoutProcedure,
  sequentialFallbackEdges,
  type ProcedureLayoutStep
} from "../procedureLayout";

interface ProcedureFlowStep extends ProcedureLayoutStep {
  code: string;
  name: string;
  type: string;
  operation?: string | null;
}

const props = withDefaults(defineProps<{
  steps: ProcedureFlowStep[];
  edges?: { fromStepId: string; toStepId: string }[];
  outcomes?: Record<string, string>;
  currentStepId?: string | null;
  selectedStepId?: string | null;
  height?: number;
  flowId?: string;
  markers?: Record<string, "added" | "changed">;
}>(), {
  edges: () => [],
  outcomes: () => ({}),
  currentStepId: null,
  selectedStepId: null,
  height: 320,
  flowId: "procedure-flow",
  markers: () => ({})
});

const emit = defineEmits<{ select: [stepId: string] }>();
const nodeTypes = { recipeStep: markRaw(StepFlowNode) as unknown as NodeComponent };

const layoutSteps = computed(() => props.steps);
const lanes = computed(() => collectLanes(layoutSteps.value));
const hasMarkers = computed(() => Object.keys(props.markers ?? {}).length > 0);

const topologyEdges = computed(() => {
  const mapped = (props.edges ?? []).map((e) => ({ from: e.fromStepId, to: e.toStepId }));
  return mapped.length ? mapped : sequentialFallbackEdges(layoutSteps.value);
});

const positions = computed(() => layoutProcedure(layoutSteps.value, topologyEdges.value));

function isLive(outcome?: string, id?: string) {
  if (outcome === "Running" || outcome === "AwaitingConfirm" || outcome === "Held") return true;
  return !!id && id === props.currentStepId && outcome !== "Completed" && outcome !== "Skipped" && outcome !== "Faulted";
}

const nodes = computed<Node[]>(() =>
  props.steps.map((s) => {
    const outcome = props.outcomes[s.id] ?? "Pending";
    const pos = positions.value[s.id] ?? { x: 80, y: 48 };
    return {
      id: s.id,
      type: "recipeStep",
      position: pos,
      data: {
        label: `${s.code} ${s.name}`,
        code: s.code,
        name: s.name,
        type: s.type,
        operation: s.operation,
        unitProcedure: s.unitProcedure,
        outcome,
        current: isLive(outcome, s.id),
        picked: s.id === props.selectedStepId,
        change: props.markers?.[s.code]
      },
      selected: s.id === props.selectedStepId
    };
  })
);

const graphEdges = computed<Edge[]>(() =>
  topologyEdges.value.map((e, i) => {
    const fromOut = props.outcomes[e.from] ?? "Pending";
    const toOut = props.outcomes[e.to] ?? "Pending";
    const live = fromOut === "Completed" && isLive(toOut, e.to);
    const done = fromOut === "Completed" && toOut === "Completed";
    return {
      id: `e${i}-${e.from}-${e.to}`,
      source: e.from,
      target: e.to,
      type: "smoothstep",
      animated: live,
      style: { stroke: done ? "var(--ok)" : live ? "var(--accent)" : "var(--idle)" }
    };
  })
);

function onNodeClick({ node }: NodeMouseEvent) {
  emit("select", node.id);
}
</script>

<style scoped>
.lanes { display: flex; gap: var(--space-2); flex-wrap: wrap; margin-bottom: var(--space-3); }
.lane-chip {
  font-size: 11px; color: var(--muted); border: 1px dashed var(--accent);
  border-radius: 999px; padding: 2px var(--space-3);
}
/* 图例是画布的读图钥匙，与泳道条、画布保持同一档间距，避免三段各自为政 */
.legend { display: flex; gap: var(--space-4); flex-wrap: wrap; font-size: 11px; color: var(--muted); margin-bottom: var(--space-3); }
.legend span { display: inline-flex; align-items: center; gap: 6px; }
.lg { width: 8px; height: 8px; border-radius: 50%; display: inline-block; background: var(--idle); flex: none; }
.lg.pending { background: var(--idle); }
.lg.run { background: var(--accent); }
.lg.wait { background: var(--warn); }
.lg.ok { background: var(--ok); }
.lg.skip { background: var(--muted); }
.lg.err { background: var(--err); }
.lg.added { background: var(--ok); }
.lg.changed { background: var(--warn); }
.canvas {
  border: 1px solid var(--line);
  border-radius: 8px;
  overflow: hidden;
  background: var(--sunken);
}
.empty { height: 100%; display: flex; align-items: center; justify-content: center; color: var(--muted); font-size: 13px; }
:deep(.vue-flow) { background: var(--sunken); }
:deep(.vue-flow__edge-path) { stroke-width: 1.6; }
:deep(.vue-flow__controls) { box-shadow: none; border: 1px solid var(--line); border-radius: 6px; overflow: hidden; }
:deep(.vue-flow__controls-button) {
  background: var(--raised);
  border-bottom: 1px solid var(--line);
  fill: var(--text-body);
}
/* 每个按钮都带 border-bottom，最后一个会在控制条底边留下一条多余的线 */
:deep(.vue-flow__controls-button:last-child) { border-bottom: 0; }
:deep(.vue-flow__controls-button:hover) { background: var(--hover); }
:deep(.vue-flow__minimap) { background: var(--panel); }
</style>
