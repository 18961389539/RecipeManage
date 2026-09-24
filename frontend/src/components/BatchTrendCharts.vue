<template>
  <el-card>
    <template #header>
      <div class="trend-head">
        <span>{{ $t("实时工艺趋势") }}</span>
        <el-radio-group v-model="chartKind" size="small">
          <el-radio-button value="echarts">ECharts</el-radio-button>
          <el-radio-button value="uplot">uPlot</el-radio-button>
        </el-radio-group>
      </div>
    </template>
    <div v-show="chartKind === 'echarts'" ref="chartEl" class="trend-chart" />
    <div v-show="chartKind === 'uplot'" ref="uplotEl" class="trend-chart" />
    <p v-if="note" class="trend-note">{{ note }}</p>
  </el-card>
</template>

<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import echarts, { type EChartsType } from "../utils/echarts";
import uPlot from "uplot";
import "uplot/dist/uPlot.min.css";
import { signalLabel, signalUnit } from "../utils/labels";
import { palette } from "../utils/theme";

/**
 * 实时工艺趋势。两种渲染器并存是历史包袱也是取舍：ECharts 有 tooltip 与双轴，
 * uPlot 在 100ms 级高频采样下明显更省，所以默认 uPlot、留一个切换口。
 *
 * series 是普通对象（不是 reactive）：高频 push 不想触发 Vue 依赖追踪，
 * 所以重绘由父组件在取数/采样事件后显式调 refresh()。
 */
const props = defineProps<{
  series: Record<string, [number, number][]>;
  /** 趋势 tab 是否可见：不可见时不渲染，uPlot 在 0 宽容器里会画歪。 */
  active: boolean;
  /** 取数口径说明（抽稀比例）。降采样后的曲线必须标注，否则用户会把它当成全量数据读。 */
  note?: string;
}>();

const chartEl = ref<HTMLDivElement | null>(null);
const uplotEl = ref<HTMLDivElement | null>(null);
const chartKind = ref<"echarts" | "uplot">("uplot");
let chart: EChartsType | null = null;
let plot: uPlot | null = null;

function renderChart() {
  if (!props.active) return;
  if (chartKind.value === "uplot") {
    renderUplot();
    return;
  }
  if (!chartEl.value) return;
  chart ??= echarts.init(chartEl.value);
  const entries = Object.entries(props.series);
  // 温度约 530、压力约 1.01，同一条 y 轴上压力会被压成一条直线，必须分左右轴。
  const isTemp = (tag: string) => signalUnit(tag) === "℃";
  const rightUnits = [...new Set(entries.filter(([t]) => !isTemp(t)).map(([t]) => signalUnit(t)).filter(Boolean))];
  chart.setOption({
    backgroundColor: "transparent",
    // containLabel：右轴是这次新加的，轴名「bar / s」和首个时间标签都会被默认 grid 裁掉。
    grid: { left: 8, right: 8, top: 46, bottom: 6, containLabel: true },
    tooltip: { trigger: "axis" },
    legend: { top: 4, data: entries.map(([tag]) => signalLabel(tag)), textStyle: { color: palette("--text-body") } },
    // hideOverlap：窄卡里时间标签会互相压成一片。默认模板在亚秒窗口里带毫秒，是有效信息，不要改掉。
    xAxis: { type: "time", axisLabel: { color: palette("--muted"), hideOverlap: true } },
    yAxis: [
      { type: "value", name: "℃", nameTextStyle: { color: palette("--muted") }, axisLabel: { color: palette("--muted") }, splitLine: { lineStyle: { color: palette("--line") } } },
      { type: "value", name: rightUnits.join(" / "), nameTextStyle: { color: palette("--muted") }, position: "right", axisLabel: { color: palette("--muted") }, splitLine: { show: false } }
    ],
    series: entries.map(([tag, data]) => ({
      name: signalLabel(tag),
      type: "line",
      showSymbol: false,
      yAxisIndex: isTemp(tag) ? 0 : 1,
      data
    }))
  });
}

function renderUplot() {
  if (!uplotEl.value) return;
  const names = Object.keys(props.series);
  const xs: number[] = [];
  const seen = new Set<number>();
  for (const pts of Object.values(props.series)) {
    for (const [t] of pts) {
      if (!seen.has(t)) {
        seen.add(t);
        xs.push(t);
      }
    }
  }
  xs.sort((a, b) => a - b);
  const data: uPlot.AlignedData = [new Float64Array(xs)];
  for (const name of names) {
    const map = new Map(props.series[name]);
    data.push(xs.map((t) => map.get(t) ?? null) as unknown as Float64Array);
  }
  plot?.destroy();
  plot = new uPlot({
    width: Math.max(uplotEl.value.clientWidth || 360, 240),
    height: 260,
    legend: { show: true },
    axes: [
      { stroke: palette("--muted"), grid: { stroke: palette("--line") } },
      { stroke: palette("--muted"), grid: { stroke: palette("--line") } }
    ],
    series: [
      {},
      ...names.map((name, i) => ({
        label: `${signalLabel(name)}${signalUnit(name) ? ` (${signalUnit(name)})` : ""}`,
        stroke: i === 0 ? palette("--warn") : palette("--cool"),
        width: 1.4
      }))
    ]
  }, data, uplotEl.value);
}

/** 切 tab / 刚显示时容器宽度还没定，所以补一次延时重绘。 */
function refresh() {
  void nextTick().then(() => {
    renderChart();
    chart?.resize();
    window.setTimeout(() => {
      renderChart();
      chart?.resize();
    }, 50);
  });
}

function onResize() {
  chart?.resize();
}

watch(chartKind, () => refresh());
watch(() => props.active, (on) => { if (on) refresh(); });
onMounted(() => {
  window.addEventListener("resize", onResize);
  if (props.active) refresh();
});
onUnmounted(() => {
  window.removeEventListener("resize", onResize);
  plot?.destroy();
  chart?.dispose();
  chart = null;
});

defineExpose({ refresh });
</script>

<style scoped>
.trend-head { display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); flex-wrap: wrap; }
.trend-chart { height: 260px; }
.trend-note { margin: var(--space-2) 0 0; color: var(--muted); font-size: 12px; line-height: 1.5; }
</style>
