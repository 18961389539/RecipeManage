<template>
  <el-card>
    <template #header>
      <div class="trend-head">
        <span>{{ $t("PLC 握手位 · {0}", [lane.equipmentCode]) }}</span>
        <span class="muted head-right">
          <!-- 位名对非 PLC 背景的操作员是噪声：可以只看当前为 1 的位（默认仍全量，诊断时不用再点） -->
          <el-checkbox v-model="onlyOn" size="small">{{ $t("只显示非 0 位") }}</el-checkbox>
          <HelpTip :term="handshakePhaseTip(lane.phase)">{{ handshakePhaseLabel(lane.phase) }}</HelpTip>
        </span>
      </div>
    </template>
    <div class="muted gap-after-sm">{{ lane.unitProcedure }} · {{ lane.stepCode }} · {{ stepOutcomeLabel(lane.outcome) }}</div>
    <div class="signal-grid">
      <div v-for="cell in shownLamps" :key="cell.label" class="signal-cell">
        <HelpTip :term="cell.label">{{ cell.label }}</HelpTip>
        <i class="dot" :class="cell.on && cell.kind ? cell.kind : ''" />
      </div>
    </div>
    <div class="signal-foot">
      <span v-for="c in counters" :key="c.label" class="signal-counter">
        <HelpTip :term="c.label">{{ c.label }}</HelpTip><b>{{ c.text }}</b>
      </span>
      <span v-if="allOff" class="signal-quiet">{{ $t("本车道握手信号当前全为 0（批次未启动或已完成）") }}</span>
    </div>
  </el-card>
</template>

<script setup lang="ts">
import { computed, ref } from "vue";
import type { LaneHandshakeDto } from "../api/types";
import { handshakePhaseLabel, handshakePhaseTip, stepOutcomeLabel } from "../utils/labels";
import { signalCounters, signalLamps, signalsAllOff, type PlcSignals } from "../utils/plcSignals";
import HelpTip from "./HelpTip.vue";

/**
 * 一条车道的握手位。灯与计数的派生规则在 utils/plcSignals（那里可单测），
 * 这里只负责铺开：7 个灯走网格、2 个数值单独一行——同排会被读成两个灰灯。
 */
const props = defineProps<{ lane: LaneHandshakeDto; signals: PlcSignals }>();

const onlyOn = ref(false);
const lamps = computed(() => signalLamps(props.signals));
/** 只看当前为 1 的位：整排 0 的位在正常运行时只是背景噪声，全量列表留给诊断场景。 */
const shownLamps = computed(() => (onlyOn.value ? lamps.value.filter((c) => c.on) : lamps.value));
const counters = computed(() => signalCounters(props.signals));
const allOff = computed(() => signalsAllOff(props.signals));
</script>

<style scoped>
.muted { color: var(--muted); font-size: 12px; }
.trend-head { display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); flex-wrap: wrap; }
.head-right { display: inline-flex; align-items: center; gap: var(--space-3); }
.signal-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(118px, 1fr)); gap: var(--space-2); }
.signal-cell {
  display: flex; align-items: center; justify-content: space-between; gap: var(--space-2);
  padding: 6px 10px; border-radius: 8px;
  background: var(--sunken); border: 1px solid var(--line);
  font-size: 12px; color: var(--muted);
}
/* 数值格单独一行：与灯网同排会被读成两个灰灯 */
.signal-foot {
  display: flex; align-items: center; flex-wrap: wrap; gap: var(--space-1) var(--space-4);
  margin-top: var(--space-2); font-size: 12px; color: var(--muted);
}
.signal-counter { display: inline-flex; align-items: baseline; gap: 6px; }
.signal-counter b { color: var(--text-body); font-weight: 600; font-variant-numeric: tabular-nums; }
.signal-quiet { color: var(--muted); margin-left: auto; }
</style>
