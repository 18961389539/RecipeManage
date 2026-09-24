import { computed, type Ref } from "vue";
import { type Connection, type Edge, type Node, type NodeChange, type NodeMouseEvent } from "@vue-flow/core";
import { LAYOUT_X0, LAYOUT_Y0, layoutProcedure } from "../procedureLayout";
import type { RecipeVersionDto } from "../api/types";

/**
 * 工艺画布：草稿的 steps/edges ↔ VueFlow 的 nodes/edges 双向映射。
 *
 * 坐标存在工步上（`canvasX/canvasY`）而不是画布私有状态里，所以拖动就是改草稿——
 * 这也是"未保存"提示的来源之一。这里只搬运位置，不判断业务规则：
 * 能不能连线由调用方注入的 `canJoin` 决定（拓扑规则在 utils/procedureTopology）。
 */
export function useFlowCanvas(options: {
  working: { readonly value: RecipeVersionDto | null };
  selectedId: Ref<string | null>;
  editable: { readonly value: boolean };
  canJoin: (fromId: string, toId: string) => boolean;
  onConnectBlocked: () => void;
}) {
  const { working, selectedId, editable } = options;

  function layoutWorking() {
    const steps = working.value?.steps ?? [];
    const edges = working.value?.edges ?? [];
    return layoutProcedure(
      steps.map((s) => ({ id: s.id, unitProcedure: s.unitProcedure, ordinal: s.ordinal })),
      edges.map((e) => ({ from: e.fromStepId, to: e.toStepId }))
    );
  }

  const nodes = computed<Node[]>(() => {
    const steps = working.value?.steps ?? [];
    const hasCanvas = steps.some((s) => s.canvasX || s.canvasY);
    const pos = hasCanvas ? null : layoutWorking();
    return steps.map((s) => ({
      id: s.id,
      type: "recipeStep",
      position: hasCanvas
        ? { x: s.canvasX, y: s.canvasY }
        : (pos?.[s.id] ?? { x: LAYOUT_X0, y: LAYOUT_Y0 }),
      data: {
        label: `${s.code} ${s.name}`, code: s.code, name: s.name,
        type: s.type, operation: s.operation, unitProcedure: s.unitProcedure, plcProgramId: s.plcProgramId
      },
      selected: s.id === selectedId.value
    }));
  });

  const edges = computed<Edge[]>(() =>
    (working.value?.edges ?? []).map((e, i) => ({
      id: e.id ?? `e${i}`,
      source: e.fromStepId,
      target: e.toStepId
    }))
  );

  /**
   * 画布高度跟着内容走。之前固定 480px，三个工步的配方会在节点下方留 300 多 px 空白，
   * 看着像画布没加载完；泳道多的长工艺又需要足够高度，所以取内容包围盒并夹在 240–560。
   * 节点实测约 112px 高，底部再留 40px 给连线与控制条。
   */
  const flowHeight = computed(() => {
    const bottom = nodes.value.reduce((max, n) => Math.max(max, (n.position?.y ?? 0) + 112), 0);
    return Math.min(560, Math.max(240, bottom + 40));
  });

  const edgeRows = computed(() =>
    (working.value?.edges ?? []).map((e) => {
      const from = working.value?.steps.find((s) => s.id === e.fromStepId);
      const to = working.value?.steps.find((s) => s.id === e.toStepId);
      return {
        fromId: e.fromStepId,
        toId: e.toStepId,
        fromCode: from?.code ?? e.fromStepId.slice(0, 8),
        toCode: to?.code ?? e.toStepId.slice(0, 8)
      };
    })
  );

  function autoLayout() {
    if (!working.value) return;
    const pos = layoutWorking();
    for (const s of working.value.steps) {
      const p = pos[s.id];
      if (!p) continue;
      s.canvasX = p.x;
      s.canvasY = p.y;
    }
  }

  function onNodes(changes: NodeChange[]) {
    if (!working.value || !editable.value) return;
    for (const change of changes) {
      if (change.type === "position" && change.position) {
        const step = working.value.steps.find((s) => s.id === change.id);
        if (step) {
          step.canvasX = change.position.x;
          step.canvasY = change.position.y;
        }
      }
      if (change.type === "select" && change.selected)
        selectedId.value = change.id;
    }
  }

  function onConnect(c: Connection) {
    if (!working.value || working.value.status !== "Draft" || !c.source || !c.target) return;
    if (c.source === c.target) return;
    if (working.value.edges.some((e) => e.fromStepId === c.source && e.toStepId === c.target))
      return;
    if (!options.canJoin(c.source, c.target)) {
      options.onConnectBlocked();
      return;
    }
    working.value.edges.push({ fromStepId: c.source, toStepId: c.target });
  }

  function onNodeClick({ node }: NodeMouseEvent) {
    selectedId.value = node.id;
  }

  return { nodes, edges, flowHeight, edgeRows, layoutWorking, autoLayout, onNodes, onConnect, onNodeClick };
}
