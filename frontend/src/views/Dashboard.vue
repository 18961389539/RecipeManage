<template>
  <div class="dash-page">
    <div class="page-title">
      <h2>{{ $t("运行总览") }}</h2>
      <div class="health-pill" :class="pillClass">
        <i class="dot" :class="pillDot" />
        <HelpTip v-if="pillState === 'ok'" term="服务健康" :extra="healthDetail" plain>
          <span>{{ pillText }}</span>
        </HelpTip>
        <span v-else-if="pillState === 'stale'">{{ pillText }}</span>
        <span v-else>{{ $t("服务异常") }}</span>
      </div>
    </div>
    <div class="kpi-group">{{ $t("需处理") }}</div>
    <div class="kpi-grid">
      <el-card
        v-for="k in todoKpis"
        :key="k.key"
        class="kpi"
        :class="[kpiTone(k), { clickable: kpiClickable(k) }]"
        :role="kpiClickable(k) ? 'button' : undefined"
        :tabindex="kpiClickable(k) ? 0 : undefined"
        @click="openKpi(k)"
        @keyup.enter="openKpi(k)"
      >
        <div class="kpi-label">{{ k.label }}</div>
        <!-- 没有数据就画一个破折号，绝不画 0：0 是"确认没有故障"，— 是"不知道"。
             把不知道显示成零，等于在故障时给出一块全绿的面板。 -->
        <div class="kpi-value" :class="{ unknown: !hasData }">{{ hasData ? k.value : "—" }}</div>
        <!-- 只在"确认有事"时提醒：critical 非零才脉冲，0 和未知不打扰。 -->
        <i v-if="hasData && k.tone === 'critical' && k.value > 0" class="pulse-dot" />
      </el-card>
    </div>
    <!-- 参考组压成一行窄条：这四个数不需要动手，之前用 4 张 150px 高的卡，
         把设备占用表整块推到了首屏之外。 -->
    <div class="ref-strip">
      <span class="ref-strip-label">{{ $t("参考") }}</span>
      <button
        v-for="k in refKpis"
        :key="k.key"
        type="button"
        class="ref-item"
        :class="{ zero: hasData && k.value === 0 }"
        :disabled="!kpiClickable(k)"
        @click="openKpi(k)"
      ><span>{{ $t(k.label) }}</span><b>{{ hasData ? k.value : "—" }}</b></button>
    </div>
    <el-alert class="gap-before"
      v-if="loadError"
      :closable="false"
      type="error"
      :title="$t('运行数据取数失败：{0}', [loadError])"
      :description="hasData
        ? $t('下面的数字是最后一次成功取数的结果，已经不代表当前状态；页面每 4 秒自动重试。')
        : $t('还没有取到任何数据，所有计数显示为「—」而不是 0；页面每 4 秒自动重试。')
        "
      show-icon
     
    />
    <!-- 健康只在异常时占版面；正常态由 KPI 区那枚徽标表达，避免绿色提示条常年占位。 -->
    <el-alert class="gap-before"
      v-if="pillState === 'down'"
      :closable="false"
      type="error"
      show-icon
      :title="$t('API 健康检查失败')"
      :description="$t('后端 /health 未返回 ok，运行总览与实时推送可能均已中断。')"
     
    />
    <div class="dash-main">
      <el-card :header="$t('执行态势')" class="chart-card">
        <div ref="chartEl" class="chart-box" />
      </el-card>
      <el-card class="occupancy-card" :header="$t('设备占用（一台设备同时只允许一个运行/排队/保持批次）')">
        <el-table
          ref="occupancyTableRef"
          :data="dash?.equipmentOccupancy ?? []"
          v-loading="initialLoading"
          :empty-text="hasData ? $t('暂无设备数据') : $t('数据不可用')"
          :row-class-name="occupancyRowClass"
          class="clickable-rows"
          @row-click="openOccupant"
        >
          <el-table-column prop="code" :label="$t('设备')" width="90" fixed />
          <el-table-column prop="name" :label="$t('名称')" />
          <el-table-column prop="protocol" :label="$t('协议')" width="120">
            <template #default="{ row }">{{ protocolLabel(row.protocol) }}</template>
          </el-table-column>
          <el-table-column prop="occupancy" width="90">
            <template #header><HelpTip term="设备占用" /></template>
            <template #default="{ row }">
              <el-tag size="small" :type="occupancyTagType(row.occupancy)" effect="dark">{{ occupancyLabel(row.occupancy) }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="batchNo" :label="$t('批次')" width="160">
            <!-- 空值一律裸「—」，不套标签：空闲设备的这格此前是灰色药丸里的短横，
                 与相邻「四步握手」列的裸「—」两种长相，读起来像控件坏了。 -->
            <template #default="{ row }">{{ row.batchNo || "—" }}</template>
          </el-table-column>
          <el-table-column prop="batchStatus" :label="$t('批次状态')" width="120">
            <template #default="{ row }">
              <el-tag v-if="row.batchStatus" size="small" :type="batchStatusTagType(row.batchStatus)" effect="dark">{{ batchStatusLabel(row.batchStatus) }}</el-tag>
              <span v-else>—</span>
            </template>
          </el-table-column>
          <el-table-column prop="handshakePhase">
            <template #header><HelpTip term="四步握手" /></template>
            <template #default="{ row }">
              <HelpTip :term="handshakePhaseTip(handshakeDisplayPhase(row.batchStatus, null, row.handshakePhase))">{{ handshakePhaseLabel(handshakeDisplayPhase(row.batchStatus, null, row.handshakePhase)) }}</HelpTip>
            </template>
          </el-table-column>
        </el-table>
      </el-card>
    </div>
    <el-card class="gap-before" :header="$t('在途批次（运行 / 排队 / 保持 / 故障）')">
        <el-table
          ref="liveTableRef"
          :data="dash?.liveBatches ?? []"
          v-loading="initialLoading"
          :empty-text="hasData ? $t('当前没有在途批次') : $t('数据不可用')"
          class="clickable-rows"
          @row-click="(row: BatchListItemDto) => $router.push(`/batches/${row.id}`)"
        >
          <el-table-column prop="batchNo" :label="$t('批次号')" min-width="150" fixed />
          <el-table-column prop="recipeName" :label="$t('配方')" />
          <el-table-column prop="status" :label="$t('状态')" width="120">
            <template #default="{ row }">
              <el-tag size="small" :type="batchStatusTagType(row.status)" effect="dark">{{ batchStatusLabel(row.status) }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="currentStepIndex" :label="$t('工步')" width="90">
            <template #default="{ row }">
              <!-- 排队/已创建还没有工步可指，画「—」不画第 1 步；索引是 0 基，+1 转成人的数法。 -->
              <span v-if="row.status === 'Queued' || row.status === 'Created'">—</span>
              <span v-else>{{ $t("第 {0} 步", [row.currentStepIndex + 1]) }}</span>
            </template>
          </el-table-column>
          <el-table-column prop="handshakePhase">
            <template #header><HelpTip term="四步握手" /></template>
            <template #default="{ row }">
              <HelpTip :term="handshakePhaseTip(handshakeDisplayPhase(row.status, null, row.handshakePhase))">{{ handshakePhaseLabel(handshakeDisplayPhase(row.status, null, row.handshakePhase)) }}</HelpTip>
            </template>
          </el-table-column>
          <el-table-column prop="equipmentCode" :label="$t('设备')" width="110" />
        </el-table>
      </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from "vue";
import { useRouter } from "vue-router";
import type { TableInstance } from "element-plus";
import echarts, { type EChartsType } from "../utils/echarts";
import http, { HttpError } from "../api/http";
import type { BatchListItemDto, DashboardDto, EquipmentOccupancyDto, ExecutionEvent, HealthDto } from "../api/types";
import { occupancyFromEvent, useExecutionHub } from "../realtime/executionHub";
import { useCoalescedReload } from "../utils/useCoalescedReload";
import { usePolling } from "../utils/usePolling";
import { useKeyboardRows } from "../utils/useKeyboardRows";
import { useAuthStore } from "../stores/auth";
import { handshakeDisplayPhase } from "../utils/handshake";
import { palette, type PaletteToken } from "../utils/theme";
import { t } from "../i18n";
import HelpTip from "../components/HelpTip.vue";
import {
  batchStatusLabel,
  batchStatusTagType,
  handshakePhaseLabel,
  handshakePhaseTip,
  occupancyLabel,
  occupancyTagType,
  protocolLabel,
  databaseLabel
} from "../utils/labels";

const auth = useAuthStore();
const dash = ref<DashboardDto | null>(null);
const health = ref<HealthDto | null>(null);
// 两张磁贴表都是整行可点，但 EP 渲染的 tr 不可聚焦——键盘用户此前进不去任何批次/设备。
const occupancyTableRef = ref<TableInstance>();
const liveTableRef = ref<TableInstance>();
useKeyboardRows(occupancyTableRef, () => dash.value?.equipmentOccupancy ?? []);
useKeyboardRows(liveTableRef, () => dash.value?.liveBatches ?? []);
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

/**
 * 空闲行压暗成背景信息；占用行按批次状态在行首打 3px 色条——
 * 设备多的时候空闲行是噪音，出事的设备应该从行首色条一列扫出来。
 */
function occupancyRowClass({ row }: { row: EquipmentOccupancyDto }): string {
  if (row.occupancy !== "Occupied") return "occ-idle";
  if (row.batchStatus === "Faulted") return "occ-critical";
  if (row.batchStatus === "Held") return "occ-warn";
  return "occ-active";
}

function openBatches(query: Record<string, string>) {
  void router.push({ path: "/batches", query });
}

interface KpiItem {
  key: string;
  label: string;
  value: number;
  tone?: "critical" | "warn";
  /** 跳批次列表并带状态筛选 */
  status?: string;
  /** 额外查询（如待检终样） */
  query?: Record<string, string>;
  /** 跳指定页面 */
  path?: string;
  /** 无权限时不响应点击 */
  allowed?: boolean;
}

const healthOk = computed(() => health.value?.status === "ok");

/**
 * 健康徽标悬停里的版本与库结构水位。远程支持的第一句话是"你装的是哪版"，
 * 操作员不该为了回答它去开终端或翻 exe 属性；两个值都是 /health 原样带回来的。
 */
const healthDetail = computed(() => {
  const parts = [databaseLabel(health.value?.database)];
  if (health.value?.version) parts.push(health.value.version);
  if (health.value?.migration) parts.push(health.value.migration);
  return parts.filter(Boolean).join(" · ");
});

/** 有没有拿到过一次数据。没有的时候计数一律画「—」，不能画 0。 */
const hasData = computed(() => dash.value !== null);

/**
 * 徽标的三态。之前它只看 /health：数据接口整片 503 时进程还活着、探针还是 200，
 * 于是页面同时挂着「服务正常」的绿点和「数据加载失败」的红条——自相矛盾的两句话。
 * 取数失败优先于探针结论，因为操作员要处置的是屏幕上的数字，不是进程。
 */
const pillState = computed<"ok" | "stale" | "down">(() => {
  if (!healthOk.value) return "down";
  return loadError.value ? "stale" : "ok";
});
const pillClass = computed(() => (pillState.value === "ok" ? "" : pillState.value === "stale" ? "warn" : "bad"));
const pillDot = computed(() => (pillState.value === "ok" ? "on" : pillState.value === "stale" ? "warn" : "err"));

/** 最后一次成功取数的时刻。轮询 4 秒一次但界面不说话，用户无从知道眼前数字是几点取的。 */
const lastDataAt = ref<number | null>(null);

function fmtTime(ts: number): string {
  return new Date(ts).toLocaleTimeString(undefined, { hour12: false });
}

/**
 * 徽标文案带新鲜度：正常态「更新于 HH:mm:ss」；中断态「停在 HH:mm:ss」——
 * 数字已经不再刷新这件事，必须连同它停在几点一起说出来，否则操作员分不清
 * "刚刷过"和"停在半小时前"。
 */
const pillText = computed(() => {
  if (pillState.value === "stale") {
    return lastDataAt.value === null
      ? t("数据中断")
      : `${t("数据中断")} · ${t("停在 {0}", fmtTime(lastDataAt.value))}`;
  }
  return lastDataAt.value === null
    ? t("服务正常")
    : `${t("服务正常")} · ${t("更新于 {0}", fmtTime(lastDataAt.value))}`;
});

/** 取数失败时给人话，不要把 axios 的 "Request failed with status code 503" 贴到界面上。 */
function failureReason(e: unknown): string {
  const err = e as HttpError;
  if (typeof err?.status === "number") return t("后端返回 {0}", err.status);
  const message = err?.message ?? "";
  // 拦截器会把后端 data.message 透出来，那是已经按 Accept-Language 译过的校验/权限提示，直接用；
  // 剩下的（"Network Error"、"Request failed with status code …"）是 axios 的英文原文，换成中文说法。
  if (message && !/^(Request failed|Network Error|timeout of)/i.test(message)) return message;
  return t("无法连接后端");
}

/** 需人工处置的计数。tone 只在非零时生效 —— 0 故障不该用红色抢视线。 */
const todoKpis = computed<KpiItem[]>(() => {
  const d = dash.value;
  return [
    { key: "faulted", label: t("握手故障批次"), value: d?.faultedBatches ?? 0, tone: "critical", status: "Faulted" },
    { key: "alarms", label: t("未确认报警"), value: d?.openAlarms ?? 0, tone: "critical", path: "/alarms", allowed: canAlarms.value },
    { key: "held", label: t("保持中批次"), value: d?.heldBatches ?? 0, tone: "warn", status: "Held" },
    { key: "release", label: t("待质量放行"), value: d?.pendingReleaseBatches ?? 0, tone: "warn", status: "Completed" },
    { key: "approvals", label: t("待审核配方"), value: d?.pendingApprovals ?? 0, tone: "warn", path: "/approvals", allowed: canApprovals.value },
    { key: "labs", label: t("待检终样"), value: d?.pendingLabBatches ?? 0, tone: "warn", query: { lab: "pending" } }
  ];
});

/** 只作参考、不要求立刻处理。 */
const refKpis = computed<KpiItem[]>(() => {
  const d = dash.value;
  return [
    { key: "running", label: t("执行中批次"), value: d?.runningBatches ?? 0, status: "Running" },
    { key: "queued", label: t("排队批次"), value: d?.queuedBatches ?? 0, status: "Queued" },
    { key: "approved", label: t("已批准配方"), value: d?.approvedRecipes ?? 0, path: "/recipes", allowed: true },
    { key: "draft", label: t("草稿配方"), value: d?.draftRecipes ?? 0, path: "/recipes", allowed: true }
  ];
});

function kpiTone(k: KpiItem): string {
  if (!k.tone || k.value <= 0) return "";
  return k.tone === "critical" ? "tone-critical" : "tone-warn";
}

function kpiClickable(k: KpiItem): boolean {
  if (k.path) return k.allowed !== false;
  return !!k.status || !!k.query;
}

function openKpi(k: KpiItem) {
  if (!kpiClickable(k)) return;
  if (k.path) {
    const query = k.key === "draft" ? { status: "Draft" } : k.key === "approved" ? { status: "Approved" } : undefined;
    void router.push(query ? { path: k.path, query } : k.path);
    return;
  }
  const query: Record<string, string> = { ...k.query };
  if (k.status) query.status = k.status;
  openBatches(query);
}

async function load() {
  try {
    dash.value = (await http.get<DashboardDto>("/dashboard")).data;
    loadError.value = "";
    lastDataAt.value = Date.now();
  } catch (e) {
    // 不清 dash：已经拿到过的数字是"最后已知状态"，比抹成空白有用，前提是页面同时说明它已经不新鲜。
    loadError.value = failureReason(e);
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
  // 一次数据都没拿到时不要画 6 根 0 长条：那和"全部正常"的图长得一模一样。
  if (!hasData.value) {
    chart.clear();
    return;
  }
  const d = dash.value;
  // 按语义着色：蓝=在推进，黄=需要人但不致命，红=正在出事。
  // 六根同色的棒只能比长短，比不出"麻烦在哪根"。
  const tones: PaletteToken[] = [
    "--accent-bright", "--accent-bright",
    "--warn", "--err",
    "--warn", "--err"
  ];
  const data = [
    d?.runningBatches ?? 0,
    d?.queuedBatches ?? 0,
    d?.heldBatches ?? 0,
    d?.faultedBatches ?? 0,
    d?.pendingReleaseBatches ?? 0,
    d?.openAlarms ?? 0
  ].map((value, i) => ({ value, itemStyle: { color: palette(tones[i]) } }));
  // 横向条：卡片只有约 280px 宽，竖条的 6 个中文类名会被 ECharts 自动省略到只剩 3 个
  // （实测截图里"排队/故障/未确认报警"直接消失）。类名放到 Y 轴就永远完整。
  chart.setOption({
    backgroundColor: "transparent",
    grid: { left: 8, right: 16, top: 10, bottom: 6, containLabel: true },
    tooltip: { trigger: "axis" },
    xAxis: {
      type: "value",
      minInterval: 1,
      // 数值已经标在每条棒的右端，x 轴刻度是重复信息；200px 宽里挤着 0/5/10/…/30 反而吵。
      axisLabel: { show: false },
      axisLine: { show: false },
      axisTick: { show: false },
      splitLine: { lineStyle: { color: palette("--line"), type: "dashed" } }
    },
    yAxis: {
      type: "category",
      inverse: true,
      data: ["执行中", "排队", "保持", "故障", "待放行", "未确认报警"].map((s) => t(s)),
      axisTick: { show: false },
      axisLine: { lineStyle: { color: palette("--line") } },
      axisLabel: { color: palette("--muted"), fontSize: 11 }
    },
    series: [
      {
        type: "bar",
        barWidth: 13,
        data,
        itemStyle: { color: palette("--accent-bright") },
        label: { show: true, position: "right", color: palette("--text-body"), fontSize: 11 }
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
  // 采样点只影响趋势图，与总览计数无关。
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
.dash-page { container-type: inline-size; }
/* 6 张卡固定 6 列：之前用 auto-fill，1024 宽下断成 5+1 的孤儿行，第二行那张孤零零的
   看起来像渲染坏了。6 能被 6/3/2 整除，任何断点都不会落单。 */
.kpi-grid {
  display: grid;
  grid-template-columns: repeat(6, minmax(0, 1fr));
  gap: var(--space-3);
}
.kpi-grid > .el-card { margin-top: 0; }
@container (max-width: 800px) {
  .kpi-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); }
}
@container (max-width: 520px) {
  .kpi-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
}
/* 参考组：一行窄条。这四个数不需要动手处置，之前用 4 张 150px 高的卡，
   把设备占用表整块推到了首屏之外。 */
.ref-strip {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--space-1) var(--space-3);
  margin-top: var(--space-3);
  padding-top: var(--space-2);
  border-top: 1px solid color-mix(in srgb, var(--line) 55%, var(--muted));
}
.ref-strip-label { color: var(--muted); font-size: 12px; letter-spacing: 0.6px; }
.ref-item {
  display: inline-flex;
  align-items: baseline;
  gap: 6px;
  padding: 2px 6px;
  border: 0;
  border-radius: 5px;
  background: none;
  color: var(--text-body);
  font: inherit;
  font-size: 13px;
  cursor: pointer;
}
.ref-item:hover:not(:disabled) { background: var(--hover); }
.ref-item:disabled { cursor: default; }
.ref-item b { font-size: 15px; font-weight: 600; font-variant-numeric: tabular-nums; color: var(--text); }
/* 0 不是信息，压到次要色，别和旁边非零的数抢视线 */
.ref-item.zero b { color: var(--muted); font-weight: 500; }
/* 态势图只作形状参考，200px 宽足够；把宽度让给设备占用表，它 7 列需要横向空间 */
.dash-main {
  display: grid;
  grid-template-columns: minmax(200px, 230px) minmax(0, 1fr);
  gap: var(--space-3);
  align-items: start;
  margin-top: var(--space-3);
}
@container (max-width: 860px) {
  .dash-main { grid-template-columns: 1fr; }
}
.chart-box { height: 200px; }
/* KPI 卡：原先 <div>/<h1> 用浏览器默认样式，字号与间距偏松散且与页面节奏不一致 */
.kpi :deep(.el-card__body) {
  min-height: 76px;
  display: flex;
  flex-direction: column;
  justify-content: center;
}
/* 脉冲点固定在卡片右上角，不随文案换行移动 */
.kpi { position: relative; }
.pulse-dot {
  position: absolute;
  top: 10px;
  right: 10px;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--err);
  animation: kpi-pulse 2s ease-out infinite;
}
@keyframes kpi-pulse {
  0% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--err) 45%, transparent); }
  70% { box-shadow: 0 0 0 8px transparent; }
  100% { box-shadow: 0 0 0 0 transparent; }
}
@media (prefers-reduced-motion: reduce) {
  .pulse-dot { animation: none; }
}
/* 空闲行是背景信息：文字压到 --idle，不再和占用行抢视线；它也不可导航，手型要收回去。
   el-tag 带自己的配色，.cell 的颜色管不到它，得单独退到半透明。 */
.occupancy-card :deep(.occ-idle .cell) { color: var(--idle); }
.occupancy-card :deep(.occ-idle) { cursor: default; }
.occupancy-card :deep(.occ-idle .el-tag) { opacity: 0.55; }
/* 占用行行首 3px 色条按批次状态取色，一列扫过去就知道哪台设备在出事 */
.occupancy-card :deep(.occ-active td:first-child) { box-shadow: inset 3px 0 0 var(--ok); }
.occupancy-card :deep(.occ-warn td:first-child) { box-shadow: inset 3px 0 0 var(--warn); }
.occupancy-card :deep(.occ-critical td:first-child) { box-shadow: inset 3px 0 0 var(--err); }
.kpi-label { color: var(--muted); font-size: 13px; }
.kpi-value { margin-top: var(--space-2); font-size: 30px; font-weight: 600; letter-spacing: 0.5px; font-variant-numeric: tabular-nums; }
/* 可点击的 KPI 卡此前只有 cursor:pointer，缺悬停反馈 */
.clickable:hover { border-color: var(--accent); background-color: var(--raised); }
.kpi-group {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  color: var(--muted);
  font-size: 12px;
  margin-bottom: var(--space-2);
  letter-spacing: 0.6px;
}
.kpi-group::after {
  content: "";
  flex: 1;
  height: 1px;
  background: color-mix(in srgb, var(--line) 55%, var(--muted));
}
.kpi.tone-critical { border-color: var(--err); }
.kpi.tone-critical .kpi-value { color: var(--err); }
.kpi.tone-warn { border-color: var(--warn); }
.kpi.tone-warn .kpi-value { color: var(--warn); }
/* 「不知道」要看着像不知道：压到次要色，别用那 30px 的大字抢成"确认过是零"。 */
.kpi-value.unknown { color: var(--muted); font-weight: 500; }
.health-pill {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  padding: 6px 12px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: var(--sunken);
  color: var(--text-body);
  font-size: 13px;
}
.health-pill.bad { border-color: var(--err); color: var(--err); }
/* 进程活着但数字停在最后一帧：比红轻，比绿诚实。 */
.health-pill.warn { border-color: var(--warn); color: var(--warn); }
</style>
