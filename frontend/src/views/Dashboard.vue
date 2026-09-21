<template>
  <div>
    <div class="page-title"><h2>运行总览</h2></div>
    <div class="kpi-group">需处理</div>
    <el-row :gutter="12">
      <el-col v-for="k in todoKpis" :key="k.key" :span="4" :xs="12" :sm="8" :md="6">
        <el-card class="kpi" :class="[kpiTone(k), { clickable: kpiClickable(k) }]" @click="openKpi(k)">
          <div class="kpi-label">{{ k.label }}</div>
          <h1 class="kpi-value">{{ k.value }}</h1>
        </el-card>
      </el-col>
      <el-col :span="4" :xs="12" :sm="8" :md="6">
        <el-card class="kpi health" :class="{ bad: !healthOk }">
          <div class="kpi-label">服务健康</div>
          <div class="health-line">
            <i class="dot" :class="healthOk ? 'on' : 'err'" />
            <span>{{ healthOk ? (health?.database ?? "sqlite") : "异常" }}</span>
          </div>
        </el-card>
      </el-col>
    </el-row>
    <div class="kpi-group gap-before">参考</div>
    <el-row :gutter="12">
      <el-col v-for="k in refKpis" :key="k.key" :span="4" :xs="12" :sm="8" :md="6">
        <el-card class="kpi ref" :class="{ clickable: kpiClickable(k) }" @click="openKpi(k)">
          <div class="kpi-label">{{ k.label }}</div>
          <h1 class="kpi-value">{{ k.value }}</h1>
        </el-card>
      </el-col>
      <el-col :span="8" :xs="24" :sm="24">
        <el-card header="执行态势（ECharts）">
          <div ref="chartEl" style="height:140px" />
        </el-card>
      </el-col>
    </el-row>
    <el-alert class="gap-before"
      v-if="loadError"
      :closable="false"
      type="error"
      :title="`数据加载失败：${loadError}`"
      description="页面会每 4 秒自动重试；若持续失败请检查后端 API 是否已启动。"
      show-icon
     
    />
    <!-- 健康只在异常时占版面；正常态由 KPI 区那枚徽标表达，避免绿色提示条常年占位。 -->
    <el-alert class="gap-before"
      v-if="!healthOk"
      :closable="false"
      type="error"
      show-icon
      title="API 健康检查失败"
      description="后端 /health 未返回 ok，运行总览与实时推送可能均已中断。"
     
    />
      <el-card class="gap-before" header="设备占用（一台设备同时只允许一个运行/排队/保持批次）">
        <el-table
          :data="dash?.equipmentOccupancy ?? []"
          v-loading="initialLoading"
          empty-text="暂无设备数据"
          class="clickable-rows"
          @row-click="openOccupant"
        >
          <el-table-column prop="code" label="设备" width="90" fixed />
          <el-table-column prop="name" label="名称" />
          <el-table-column prop="protocol" label="协议" width="120">
            <template #default="{ row }">{{ protocolLabel(row.protocol) }}</template>
          </el-table-column>
          <el-table-column prop="occupancy" width="90">
            <template #header><HelpTip term="设备占用" /></template>
            <template #default="{ row }">
              <el-tag size="small" :type="occupancyTagType(row.occupancy)" effect="dark">{{ occupancyLabel(row.occupancy) }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="batchNo" label="批次" width="160" />
          <el-table-column prop="batchStatus" label="批次状态" width="120">
            <template #default="{ row }">
              <el-tag size="small" :type="batchStatusTagType(row.batchStatus)" effect="dark">{{ batchStatusLabel(row.batchStatus) }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="handshakePhase">
            <template #header><HelpTip term="四步握手" /></template>
            <template #default="{ row }">
              <HelpTip :term="handshakePhaseLabel(row.handshakePhase)">{{ handshakePhaseLabel(row.handshakePhase) }}</HelpTip>
            </template>
          </el-table-column>
        </el-table>
      </el-card>
      <el-card class="gap-before" header="实时批次">
        <el-table
          :data="dash?.liveBatches ?? []"
          v-loading="initialLoading"
          empty-text="当前没有执行中的批次"
          class="clickable-rows"
          @row-click="(row: BatchListItemDto) => $router.push(`/batches/${row.id}`)"
        >
          <el-table-column prop="batchNo" label="批次号" min-width="150" fixed />
          <el-table-column prop="recipeName" label="配方" />
          <el-table-column prop="status" label="状态" width="120">
            <template #default="{ row }">
              <el-tag size="small" :type="batchStatusTagType(row.status)" effect="dark">{{ batchStatusLabel(row.status) }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="handshakePhase">
            <template #header><HelpTip term="四步握手" /></template>
            <template #default="{ row }">
              <HelpTip :term="handshakePhaseLabel(row.handshakePhase)">{{ handshakePhaseLabel(row.handshakePhase) }}</HelpTip>
            </template>
          </el-table-column>
          <el-table-column prop="equipmentCode" label="设备" width="110" />
        </el-table>
      </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from "vue";
import { useRouter } from "vue-router";
import echarts, { type EChartsType } from "../utils/echarts";
import http from "../api/http";
import type { BatchListItemDto, DashboardDto, EquipmentOccupancyDto, ExecutionEvent, HealthDto } from "../api/types";
import { occupancyFromEvent, useExecutionHub } from "../realtime/executionHub";
import { useCoalescedReload } from "../utils/useCoalescedReload";
import { usePolling } from "../utils/usePolling";
import { useAuthStore } from "../stores/auth";
import HelpTip from "../components/HelpTip.vue";
import { palette } from "../utils/theme";
import {
  batchStatusLabel,
  batchStatusTagType,
  handshakePhaseLabel,
  occupancyLabel,
  occupancyTagType,
  protocolLabel
} from "../utils/labels";

const auth = useAuthStore();
const dash = ref<DashboardDto | null>(null);
const health = ref<HealthDto | null>(null);
// 仅首屏展示加载态；轮询刷新不再闪 loading，避免 4s 一次的视觉抖动。
const initialLoading = ref(true);
const loadError = ref("");
const chartEl = ref<HTMLDivElement | null>(null);
const router = useRouter();
const canApprovals = computed(() => auth.can("Supervisor", "Quality"));
const canAlarms = computed(() => auth.can("Admin", "Operator", "Supervisor", "Quality"));
let chart: EChartsType | null = null;
// 轮询也必须走合并窗口：绕过 scheduleReload 会和事件驱动的重拉叠打出重复请求。
const poll = usePolling(() => scheduleReload());

function openOccupant(row: EquipmentOccupancyDto) {
  if (row.batchId) router.push(`/batches/${row.batchId}`);
}

function openBatches(status: string) {
  void router.push({ path: "/batches", query: { status } });
}

interface KpiItem {
  key: string;
  label: string;
  value: number;
  tone?: "critical" | "warn";
  /** 跳批次列表并带状态筛选 */
  status?: string;
  /** 跳指定页面 */
  path?: string;
  /** 无权限时不响应点击 */
  allowed?: boolean;
}

const healthOk = computed(() => health.value?.status === "ok");

/** 需人工处置的计数。tone 只在非零时生效 —— 0 故障不该用红色抢视线。 */
const todoKpis = computed<KpiItem[]>(() => {
  const d = dash.value;
  return [
    { key: "faulted", label: "握手故障批次", value: d?.faultedBatches ?? 0, tone: "critical", status: "Faulted" },
    { key: "alarms", label: "未确认报警", value: d?.openAlarms ?? 0, tone: "critical", path: "/alarms", allowed: canAlarms.value },
    { key: "release", label: "待质量放行", value: d?.pendingReleaseBatches ?? 0, tone: "warn", status: "Completed" },
    { key: "approvals", label: "待审核配方", value: d?.pendingApprovals ?? 0, tone: "warn", path: "/approvals", allowed: canApprovals.value },
    { key: "labs", label: "待检终样", value: d?.pendingLabSamples ?? 0, tone: "warn", path: "/lots" }
  ];
});

/** 只作参考、不要求立刻处理。 */
const refKpis = computed<KpiItem[]>(() => {
  const d = dash.value;
  return [
    { key: "running", label: "执行中批次", value: d?.runningBatches ?? 0, status: "Running" },
    { key: "queued", label: "排队批次", value: d?.queuedBatches ?? 0, status: "Queued" },
    { key: "approved", label: "已批准配方", value: d?.approvedRecipes ?? 0 },
    { key: "draft", label: "草稿版本", value: d?.draftRecipes ?? 0 }
  ];
});

function kpiTone(k: KpiItem): string {
  if (!k.tone || k.value <= 0) return "";
  return k.tone === "critical" ? "tone-critical" : "tone-warn";
}

function kpiClickable(k: KpiItem): boolean {
  if (k.path) return k.allowed !== false;
  return !!k.status;
}

function openKpi(k: KpiItem) {
  if (!kpiClickable(k)) return;
  if (k.path) {
    void router.push(k.path);
    return;
  }
  if (k.status) openBatches(k.status);
}

async function load() {
  try {
    dash.value = (await http.get<DashboardDto>("/dashboard")).data;
    loadError.value = "";
  } catch (e) {
    // 原先首屏取数失败会静默留一堆 0，看起来像"真没数据"，这里显式报错。
    loadError.value = (e as Error).message || "运行总览数据加载失败";
  } finally {
    initialLoading.value = false;
  }
  try {
    health.value = (await http.get<HealthDto>("/health")).data;
  } catch {
    health.value = { status: "unhealthy" };
  }
  await nextTick();
  renderChart();
}

function renderChart() {
  if (!chartEl.value) return;
  chart ??= echarts.init(chartEl.value);
  const d = dash.value;
  chart.setOption({
    backgroundColor: "transparent",
    grid: { left: 8, right: 8, top: 24, bottom: 24, containLabel: true },
    tooltip: { trigger: "axis" },
    xAxis: {
      type: "category",
      data: ["执行中", "排队", "故障", "待放行", "未确认报警"],
      axisLabel: { color: palette("--muted"), fontSize: 11 }
    },
    yAxis: { type: "value", minInterval: 1, axisLabel: { color: palette("--muted") }, splitLine: { lineStyle: { color: palette("--line") } } },
    series: [
      {
        type: "bar",
        barWidth: 22,
        data: [
          d?.runningBatches ?? 0,
          d?.queuedBatches ?? 0,
          d?.faultedBatches ?? 0,
          d?.pendingReleaseBatches ?? 0,
          d?.openAlarms ?? 0
        ],
        itemStyle: { color: palette("--accent-bright") }
      }
    ]
  });
}

// 事件不带总览数据，只能整页重拉；握手事件约 100ms 一条，必须合并后再拉。
const scheduleReload = useCoalescedReload(load);

function onExecution(evt: ExecutionEvent) {
  const occupancy = occupancyFromEvent(evt);
  if (occupancy) {
    // 占用事件自带全量占用表，就地补丁即可，不必再拉一次总览。
    if (dash.value) dash.value = { ...dash.value, equipmentOccupancy: occupancy };
    return;
  }
  // 采样点只影响趋势图，与总览的 5 个计数无关。
  if (evt.type === "sample") return;
  scheduleReload();
}

useExecutionHub({ onExecution });

onMounted(async () => {
  await load();
  poll.start();
  window.addEventListener("resize", onResize);
});

function onResize() {
  chart?.resize();
}

onUnmounted(() => {
  window.removeEventListener("resize", onResize);
  chart?.dispose();
  chart = null;
});
</script>

<style scoped>
.clickable { cursor: pointer; }
/* KPI 卡：原先 <div>/<h1> 用浏览器默认样式，字号与间距偏松散且与页面节奏不一致 */
.kpi { transition: border-color 0.18s ease, background-color 0.18s ease, transform 0.18s ease; }
.kpi-label { color: var(--muted); font-size: 13px; }
.kpi-value { margin-top: var(--space-2); font-size: 30px; font-weight: 600; letter-spacing: 0.5px; font-variant-numeric: tabular-nums; }
/* 可点击的 KPI 卡此前只有 cursor:pointer，缺悬停反馈 */
.clickable:hover { border-color: var(--accent); background-color: var(--raised); transform: translateY(-2px); }
.kpi-group { color: var(--muted); font-size: 12px; margin-bottom: var(--space-2); letter-spacing: 0.3px; }
.kpi.tone-critical { border-color: var(--err); }
.kpi.tone-critical .kpi-value { color: var(--err); }
.kpi.tone-warn { border-color: var(--warn); }
.kpi.tone-warn .kpi-value { color: var(--warn); }
/* 参考组降一档：底色凹进去、字号与字重都收一档，不与会报警的指标抢权重 */
.kpi.ref { background: var(--sunken); }
.kpi.ref .kpi-value { font-size: 24px; font-weight: 500; color: var(--text-body); }
.kpi.health .health-line { display: flex; align-items: center; gap: var(--space-2); margin-top: var(--space-3); font-size: 13px; color: var(--text-body); }
.kpi.health.bad .health-line { color: var(--err); }
</style>
