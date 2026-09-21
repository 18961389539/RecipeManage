<template>
  <!-- 原先根节点是 v-if="batch"：详情取数失败会留下整页空白且无任何提示。 -->
  <div class="page-state" v-if="error || (!batch && loading)">
    <el-alert
      v-if="error"
      type="error"
      show-icon
      :closable="false"
      :title="`批次详情加载失败：${error}`"
      description="请确认该批次是否存在，或返回批次列表重试。"
    />
    <el-skeleton v-else :rows="8" animated />
  </div>
  <div v-if="batch">
    <div class="page-title">
      <div>
        <h2>{{ batch.batchNo }} · {{ batch.snapshot.recipeName }} v{{ batch.snapshot.versionNumber }}</h2>
        <span>{{ batch.equipmentName }} · {{ batchStatusLabel(batch.status) }} · {{ handshakeSummary }}{{ livePlcLabel }} · 快照 {{ integrityLabel }}{{ scaleLabel }}</span>
      </div>
      <div>
        <el-button v-if="canConfirm && batch.status === 'Running' && awaitingConfirm" type="primary" :loading="busyAction === 'confirm'" @click="confirmStep">人工确认</el-button>
        <el-button v-if="canOperate && (batch.status === 'Created' || batch.status === 'Faulted')" type="primary" :loading="busyAction === 'start'" @click="start">{{ batch.status === 'Faulted' ? '故障后重新排队' : '启动执行' }}</el-button>
        <el-button v-if="canOperate && (batch.status === 'Running' || batch.status === 'Queued')" :loading="busyAction === 'hold'" @click="hold">保持</el-button>
        <el-button v-if="canResume && batch.status === 'Held'" type="primary" :loading="busyAction === 'resume'" @click="resume">恢复执行</el-button>
        <el-button v-if="canSkip && (batch.status === 'Held' || batch.status === 'Faulted' || (batch.status === 'Running' && skipReady))" :loading="busyAction === 'skip'" @click="skip">跳过当前工步</el-button>
        <el-button v-if="canOperate && (batch.status === 'Running' || batch.status === 'Queued' || batch.status === 'Held')" type="danger" :loading="busyAction === 'abort'" @click="abort">中止</el-button>
        <el-button @click="$router.push('/batches')">返回批次列表</el-button>
        <el-button @click="$router.push(`/batches/${batch.id}/record`)">电子批记录</el-button>
      </div>
    </div>
    <el-alert class="gap-after"
      v-if="batch.status === 'Completed'"
      :closable="false"
      type="warning"
      show-icon
      title="工艺执行与四步握手已完成。请质量在电子批记录对照归档质检后电子签名放行。"
     
    />
    <el-alert class="gap-after"
      v-if="batch.status === 'Released'"
      :closable="false"
      type="success"
      show-icon
      :title="`质量已放行${batch.releasedBy ? ` · ${batch.releasedBy}` : ''}${batch.releaseComment ? ` · ${batch.releaseComment}` : ''}`"
     
    />
    <el-alert class="gap-after"
      v-if="batch.status === 'DispositionRejected'"
      :closable="false"
      type="error"
      show-icon
      :title="`质量拒收${batch.releaseComment ? ` · ${batch.releaseComment}` : ''}`"
     
    />

    <div class="handshake-steps" :class="{ faulted: handshakeFaulted }">
      <template v-for="(s, i) in handshakeSteps" :key="s.key">
        <div v-if="i > 0" class="step-link" :class="{ done: s.state === 'done' || s.state === 'active' }" />
        <div class="handshake-step" :class="s.state">
          <i class="step-node">{{ s.state === "done" ? "✓" : s.key }}</i>
          <div class="step-text">
            <b>{{ s.title }}</b>
            <span>{{ s.hint }}</span>
          </div>
        </div>
      </template>
    </div>
    <div class="handshake-status">{{ handshakeStatusText }}</div>

    <el-card class="gap-before" header="控制配方快照 · 工艺画布（冻结拓扑，禁止盲写）">
      <p class="muted">与设计态同一套 Vue Flow 节点。坐标按快照 Procedure/Edges 自动排布（不写入完整性哈希）。点击工步可筛选质检。</p>
      <ProcedureFlow
        flow-id="batch-monitor-flow"
        :steps="flowSteps"
        :edges="flowEdges"
        :outcomes="flowOutcomes"
        :current-step-id="batch.currentStepId"
        :selected-step-id="pickedStep"
        :height="300"
        @select="pickedStep = $event"
      />
    </el-card>

    <el-card class="gap-before" header="冻结 Setpoints 矩阵（Control Recipe Snapshot · 设定 vs 归档实测）">
      <p class="muted">批次创建时从生效主配方冻结，执行中禁止改写。工步完成握手步骤 D 后，单元格显示归档实测并对照规格判定超差。</p>
      <SetpointMatrix
        :steps="matrixSteps"
        :selected-id="pickedStep"
        :current-step-id="batch.currentStepId"
        :measured="measured"
        readonly
        :max-height="280"
        @select="pickedStep = $event"
      />
    </el-card>

    <el-card class="gap-before" header="PLC 写参计划（与调度引擎同源 · 拓扑顺序）">
      <p class="muted">启动前即可核对 Step_ID / Params。Wait、人工确认、质检工步禁止写 PLC；写参工步仅在 PLC_Ready 后下发并回读。</p>
      <PlcWritePlan :items="batch.writePlan ?? []" :current-step-id="batch.currentStepId" />
    </el-card>

    <el-row class="gap-before" :gutter="12">
      <el-col :span="8" :xs="24">
        <el-card header="ISA-88 工步（按 Unit Procedure）">
          <div v-for="group in unitGroups" :key="group.name" class="up-group">
            <div class="up-title">{{ group.name }}{{ unitEquipmentLabel(group.name) }}</div>
            <el-timeline>
              <el-timeline-item v-for="s in group.steps" :key="s.stepId" :type="timelineType(s.stepId)">
                <div class="step-line" @click="pickedStep = s.stepId">
                  {{ s.code }} {{ s.name }} · {{ stepOutcomeLabel(outcome(s.stepId)) }}
                  <div class="muted">{{ s.operation || s.type }}</div>
                </div>
              </el-timeline-item>
            </el-timeline>
          </div>
          <el-divider />
          <div v-if="qualityRows.length">
            <b>工步归档质检</b>
            <el-table class="gap-before-sm" :data="qualityRows" size="small">
              <el-table-column prop="step" label="工步" width="70" fixed />
              <el-table-column prop="tag" label="测点" />
              <el-table-column prop="value" label="实测" />
              <el-table-column prop="spec" label="规格" width="90">
                <template #default="{ row }">
                  <span :style="{ color: row.oos ? 'var(--err)' : 'var(--ok)' }">{{ row.spec }}</span>
                </template>
              </el-table-column>
            </el-table>
          </div>
          <div v-else class="muted">完成握手步骤 D 后，此处显示该工步归档实测。点击时间线工步可筛选。</div>
        </el-card>
      </el-col>
      <el-col :span="8" :xs="24">
        <div v-if="displayLanes.length > 1" class="lane-tabs">
          <button
            v-for="lane in displayLanes"
            :key="lane.equipmentCode"
            type="button"
            class="lane-tab"
            :class="{ active: (selectedLaneCode || displayLanes[0].equipmentCode) === lane.equipmentCode }"
            @click="selectLane(lane.equipmentCode)"
          >{{ lane.equipmentCode }}</button>
        </div>
        <el-card class="gap-after" v-if="selectedLane">
          <template #header>
            <div class="trend-head">
              <span>PLC 握手位 · {{ selectedLane.equipmentCode }}</span>
              <span class="muted"><HelpTip :term="handshakePhaseLabel(selectedLane.phase)">{{ handshakePhaseLabel(selectedLane.phase) }}</HelpTip></span>
            </div>
          </template>
          <div class="muted gap-after-sm">{{ selectedLane.unitProcedure }} · {{ selectedLane.stepCode }} · {{ stepOutcomeLabel(selectedLane.outcome) }}</div>
          <div class="signal-grid">
            <div v-for="cell in laneSignalCells" :key="cell.label" class="signal-cell">
              <span>{{ cell.label }}</span>
              <i v-if="cell.kind" class="dot" :class="{ [cell.kind]: cell.on }" />
              <span v-else class="signal-value">{{ cell.text }}</span>
            </div>
          </div>
        </el-card>
        <el-alert class="gap-after" v-if="awaitingConfirm && batch.status === 'Running'" type="info" title="等待人工确认" description="本工步禁止写 PLC。操作员 / 质量电子签名确认后才进入下一步。" />
        <el-alert v-if="batch.status === 'Held'" type="warning" :title="batch.faultMessage || '批次保持'" :description="heldHint" />
        <el-alert v-else-if="batch.faultMessage" type="error" :title="batch.faultMessage" :description="faultHint" />
        <el-card class="gap-before" v-if="alarms.length" header="过程报警">
          <el-table :data="alarms" size="small">
            <el-table-column prop="stepCode" label="工步" width="70" fixed />
            <el-table-column prop="code" label="代码" width="120" />
            <el-table-column prop="message" label="说明" />
            <el-table-column label="确认" width="100">
              <template #default="{ row }">
                <el-button v-if="!row.acknowledgedAt && canAckAlarm" link type="primary" :loading="acking" @click="ackAlarm(row)">确认</el-button>
                <span v-else>{{ row.acknowledgedBy || "—" }}</span>
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
      <el-col :span="8" :xs="24">
        <el-card>
          <template #header>
            <div class="trend-head">
              <span>实时工艺趋势</span>
              <el-radio-group v-model="chartKind" size="small">
                <el-radio-button value="echarts">ECharts</el-radio-button>
                <el-radio-button value="uplot">uPlot</el-radio-button>
              </el-radio-group>
            </div>
          </template>
          <div v-show="chartKind === 'echarts'" ref="chartEl" style="height:260px" />
          <div v-show="chartKind === 'uplot'" ref="uplotEl" style="height:260px" />
        </el-card>
      </el-col>
    </el-row>
    <el-row class="gap-before" :gutter="12">
      <el-col :span="14" :xs="24">
        <el-card header="握手时序（禁止盲写）">
          <el-table :data="handshakeLog" size="small" max-height="240">
            <el-table-column prop="stepCode" label="工步" width="70" fixed />
            <el-table-column prop="phase" label="阶段" width="150" />
            <el-table-column prop="kind" label="动作" width="90" />
            <el-table-column prop="detail" label="说明" />
            <el-table-column prop="remainingSeconds" label="剩余s" width="70">
              <template #default="{ row }">{{ row.remainingSeconds == null ? "—" : Number(row.remainingSeconds).toFixed(0) }}</template>
            </el-table-column>
          </el-table>
          <div v-if="!handshakeLog.length" class="muted">启动批次后，此处按 PLC_Ready → Trigger_Write → Step_Running → Step_Complete 记录每一次合法动作。</div>
        </el-card>
      </el-col>
      <el-col :span="10" :xs="24">
        <el-card header="快照 vs 当前生效主配方">
          <el-alert class="gap-after-sm"
            v-if="hasDrift"
            :closable="false"
            type="warning"
            show-icon
            title="控制配方快照已冻结。主配方升版只体现在漂移列，禁止改写本批次写参。"
           
          />
          <el-table :data="drifts" size="small" max-height="240" :row-class-name="driftRowClass">
            <el-table-column prop="stepCode" label="工步" width="70" fixed />
            <el-table-column prop="parameter" label="参数" />
            <el-table-column prop="frozenSetpoint" label="快照" width="80" />
            <el-table-column prop="masterSetpoint" label="主配方" width="80" />
            <el-table-column label="漂移" width="70">
              <template #default="{ row }">{{ row.drifted ? "是" : "否" }}</template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
    </el-row>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from "vue";
import { useRoute } from "vue-router";
import echarts, { type EChartsType } from "../../utils/echarts";
import uPlot from "uplot";
import "uplot/dist/uPlot.min.css";
import { ElMessage, ElMessageBox } from "element-plus";
import http from "../../api/http";
import { esignPassword } from "../../utils/esign";
import type { BatchDetailDto, EquipmentDto, ExecutionEvent, HandshakeLogDto, LaneHandshakeDto, ProcessAlarmDto, SampleDto, SnapshotDriftDto } from "../../api/types";
import { useAuthStore } from "../../stores/auth";
import { useExecutionHub } from "../../realtime/executionHub";
import ProcedureFlow from "../../components/ProcedureFlow.vue";
import SetpointMatrix from "../../components/SetpointMatrix.vue";
import PlcWritePlan from "../../components/PlcWritePlan.vue";
import { snapshotToMatrixSteps, measuredFromExecutions, qualityReadings, formatReadingSpec, formatReadingValue } from "../../setpointMatrix";
import { batchStatusLabel, handshakePhaseLabel, signalLabel, signalUnit, stepOutcomeLabel } from "../../utils/labels";
import { snapshotIntegrityLabel } from "../../utils/integrity";
import { useLoad } from "../../utils/useLoad";
import { useCoalescedReload } from "../../utils/useCoalescedReload";
import { usePolling } from "../../utils/usePolling";
import { palette } from "../../utils/theme";
import HelpTip from "../../components/HelpTip.vue";

const route = useRoute();
const auth = useAuthStore();
const batch = ref<BatchDetailDto | null>(null);
const phase = ref("");
const chartEl = ref<HTMLDivElement | null>(null);
const uplotEl = ref<HTMLDivElement | null>(null);
const chartKind = ref<"echarts" | "uplot">("uplot");
type PlcSignals = { plcReady: boolean; stepRunning: boolean; stepComplete: boolean; stepError: boolean; errorCode: number; heartbeat: number; triggerWriteEcho: boolean; plcHeld: boolean; hostHoldEcho: boolean };
const emptySignals = (): PlcSignals => ({ plcReady: false, stepRunning: false, stepComplete: false, stepError: false, errorCode: 0, heartbeat: 0, triggerWriteEcho: false, plcHeld: false, hostHoldEcho: false });
const signals = reactive(emptySignals());
const laneSignals = reactive<Record<string, PlcSignals>>({});
const lanes = ref<LaneHandshakeDto[]>([]);
const selectedLaneCode = ref("");
const remainingByLane = reactive<Record<string, number | null>>({});
const series: Record<string, [number, number][]> = { Temperature: [], Pressure: [] };
const pickedStep = ref<string | null>(null);
const remainingSeconds = ref<number | null>(null);
const handshakeLog = ref<HandshakeLogDto[]>([]);
const alarms = ref<ProcessAlarmDto[]>([]);
const drifts = ref<SnapshotDriftDto[]>([]);
const livePlc = ref("");
const equipmentIndex = ref<Record<string, string>>({});
const { loading, error } = useLoad();
// 操作按钮防重入：任一签名+请求在途时，对应按钮显示 loading 且禁用，避免重复提交。
const busyAction = ref<string>("");
const acking = ref(false);
let chart: EChartsType | null = null;
let plot: uPlot | null = null;
// 事件与兜底轮询都走这条：合并窗口 + 在途去重，避免一串握手事件把详情接口打满。
const scheduleReload = useCoalescedReload(load);
const poll = usePolling(scheduleReload);

function outcome(stepId: string) {
  return batch.value?.stepExecutions.find((s) => s.stepId === stepId)?.outcome ?? "Pending";
}

function timelineType(stepId: string) {
  const o = outcome(stepId);
  if (o === "Running") return "primary";
  if (o === "AwaitingConfirm") return "warning";
  if (o === "Held") return "warning";
  if (o === "Completed") return "success";
  if (o === "Skipped") return "warning";
  if (o === "Faulted") return "danger";
  return "info";
}

const displayLanes = computed(() => {
  if (lanes.value.length) return lanes.value;
  if (!batch.value) return [];
  return [{
    equipmentCode: livePlc.value || batch.value.equipmentName,
    equipmentId: batch.value.equipmentId,
    unitProcedure: "",
    stepId: batch.value.currentStepId,
    stepCode: batch.value.snapshot.steps[batch.value.currentStepIndex]?.code ?? "",
    phase: phase.value || batch.value.handshakePhase,
    outcome: "Running"
  } satisfies LaneHandshakeDto];
});

const selectedLane = computed(() =>
  displayLanes.value.find((l) => l.equipmentCode === selectedLaneCode.value) ?? displayLanes.value[0]);

const skipReady = computed(() => {
  const p = selectedLane.value?.phase || phase.value || batch.value?.handshakePhase || "";
  return p === "WaitingPlcReady" || p === "Held" || p === "AwaitingConfirm" || p === "HostWait";
});

const awaitingConfirm = computed(() => {
  const currentId = selectedLane.value?.stepId || batch.value?.currentStepId;
  const exec = batch.value?.stepExecutions.find((s) => s.stepId === currentId);
  if (exec?.outcome === "AwaitingConfirm") return true;
  const snap = batch.value?.snapshot.steps.find((s) => s.stepId === currentId);
  return snap?.type === "ManualConfirm" && exec?.outcome === "Running";
});

const heldHint = computed(() => {
  const msg = batch.value?.faultMessage ?? "";
  if (msg.includes("质检超差"))
    return "归档实测超出规格，已保持批次且禁止写下一步。质量/主管审核后恢复执行。";
  const p = selectedLane.value?.phase || phase.value || batch.value?.handshakePhase || "";
  if (p === "AwaitingConfirm" || awaitingConfirm.value)
    return "人工确认等待中被保持，未写 PLC。恢复后继续等待电子签名确认。";
  return "已写 Host_Hold 并收到 PLC_Held。恢复执行将清位并继续剩余工步时长，禁止盲写下一步。";
});

const handshakeSummary = computed(() => {
  // 多通道时按设备列出各自阶段。此前直接拼原始字符串（WaitingPlcReady），操作员看不懂。
  if (displayLanes.value.length > 1)
    return displayLanes.value.map((l) => `${l.equipmentCode}:${handshakePhaseLabel(l.phase)}`).join(" · ");
  return handshakePhaseLabel(selectedLane.value?.phase || phase.value || batch.value?.handshakePhase || "");
});

function signalsOf(code: string): PlcSignals {
  return laneSignals[code] ?? (selectedLaneCode.value === code || !code ? signals : emptySignals());
}

function selectLane(code: string) {
  selectedLaneCode.value = code;
  const inbound = laneSignals[code];
  if (inbound) Object.assign(signals, inbound);
  const lane = lanes.value.find((l) => l.equipmentCode === code);
  if (lane?.phase) phase.value = lane.phase;
  remainingSeconds.value = remainingByLane[code] ?? remainingSeconds.value;
}

interface SignalCell {
  label: string;
  /** 灯的类型；缺省表示这是一格纯数值（Error_Code / Heartbeat）。 */
  kind?: "on" | "run" | "err";
  on?: boolean;
  text?: string;
}

/** 选中车道的 PLC 信号，按 2 列网格铺开，避免 8 行竖排把卡片拉长。 */
const laneSignalCells = computed<SignalCell[]>(() => {
  const s = signalsOf(selectedLane.value?.equipmentCode ?? "");
  return [
    { label: "PLC_Ready", kind: "on", on: !!s.plcReady },
    { label: "Trigger_Write", kind: "run", on: !!s.triggerWriteEcho },
    { label: "Step_Running", kind: "run", on: !!s.stepRunning },
    { label: "Step_Complete", kind: "on", on: !!s.stepComplete },
    { label: "Step_Error", kind: "err", on: !!s.stepError },
    { label: "Host_Hold", kind: "run", on: !!s.hostHoldEcho },
    { label: "PLC_Held", kind: "on", on: !!s.plcHeld },
    { label: "Error_Code", text: s.errorCode == null ? "—" : String(s.errorCode) },
    { label: "Heartbeat", text: s.heartbeat == null ? "—" : String(s.heartbeat) }
  ];
});

const flowSteps = computed(() =>
  (batch.value?.snapshot.steps ?? []).map((s) => ({
    id: s.stepId,
    code: s.code,
    name: s.name,
    type: s.type,
    unitProcedure: s.unitProcedure,
    operation: s.operation,
    ordinal: s.ordinal
  }))
);
const flowEdges = computed(() => batch.value?.snapshot.edges ?? []);
const flowOutcomes = computed(() =>
  Object.fromEntries((batch.value?.stepExecutions ?? []).map((e) => [e.stepId, e.outcome]))
);
const matrixSteps = computed(() => snapshotToMatrixSteps(batch.value?.snapshot.steps ?? []));
const measured = computed(() =>
  measuredFromExecutions(matrixSteps.value, batch.value?.stepExecutions ?? [])
);

const unitGroups = computed(() => {
  const map = new Map<string, NonNullable<typeof batch.value>["snapshot"]["steps"]>();
  for (const s of batch.value?.snapshot.steps ?? []) {
    const name = s.unitProcedure?.trim() || "UP-01 热处理单元";
    const list = map.get(name);
    if (list) list.push(s);
    else map.set(name, [s]);
  }
  return [...map.entries()].map(([name, steps]) => ({ name, steps }));
});

const qualityRows = computed(() => {
  const execs = batch.value?.stepExecutions ?? [];
  const focused = pickedStep.value ? execs.filter((s) => s.stepId === pickedStep.value) : execs;
  return qualityReadings(matrixSteps.value, focused).map((r) => ({
    step: r.stepCode,
    tag: r.tag,
    value: formatReadingValue(r),
    spec: formatReadingSpec(r),
    oos: r.oos
  }));
});

const canOperate = computed(() => auth.can("Operator", "Supervisor"));
const canSkip = computed(() => auth.can("Supervisor"));
const canConfirm = computed(() => auth.can("Operator", "Supervisor", "Quality"));
const canResume = computed(() => auth.can("Operator", "Supervisor", "Quality"));
const canAckAlarm = computed(() => auth.can("Operator", "Supervisor", "Quality"));
const hasDrift = computed(() => drifts.value.some((d) => d.drifted));
function driftRowClass({ row }: { row: SnapshotDriftDto }) {
  return row.drifted ? "drift-row" : "";
}
const integrityLabel = computed(() => snapshotIntegrityLabel(batch.value?.snapshotIntegrity));
const scaleLabel = computed(() => {
  const snap = batch.value?.snapshot;
  if (!snap) return "";
  const parts: string[] = [];
  if (snap.scaleFactor != null && snap.scaleFactor !== 1) parts.push(`缩放 ×${snap.scaleFactor}`);
  if (snap.lotNumber) parts.push(`物料 ${snap.lotNumber}`);
  const bindings = snap.unitEquipment
    ? Object.entries(snap.unitEquipment).map(([unit, id]) => `${unit}→${equipmentIndex.value[id] ?? id.slice(0, 8)}`)
    : [];
  if (bindings.length) parts.push(bindings.join("，"));
  return parts.length ? ` · ${parts.join(" · ")}` : "";
});

const livePlcLabel = computed(() => livePlc.value ? ` · ${livePlc.value}` : "");

function unitEquipmentLabel(unit: string) {
  const id = batch.value?.snapshot.unitEquipment?.[unit];
  if (!id) return "";
  const code = equipmentIndex.value[id];
  return code ? ` · ${code}` : "";
}

const faultHint = computed(() => {
  const code = batch.value?.faultCode ?? "";
  const map: Record<string, string> = {
    PlcReadyTimeout: "等待 PLC_Ready 超时：检查握手位与通讯，确认 PLC 空闲后再重新排队。",
    HeartbeatLost: "工步执行中心跳丢失：检查看门狗时间与现场心跳程序。",
    ExecutionTimeout: "工步看门狗超时：核对设定值与 PLC 程序，必要时延长该工步看门狗。",
    AckTimeout: "Trigger_Write 后未收到 Step_Running：禁止盲写下一步，先复位握手位。",
    PlcReportedError: "PLC 报 Step_Error：读取 Error_Code 后按设备手册处理。",
    BlindWriteRejected: "状态机拒绝跨阶段写参，保持四步握手顺序。",
    WriteVerifyMismatch: "写参回读与快照不一致：禁止 Trigger_Write，检查点表与 PLC 程序后再重新排队。",
    HoldAckTimeout: "Host_Hold 后未收到 PLC_Held：禁止盲写下一步，检查保持握手位。"
  };
  return map[code] ?? "可在故障清除后重新排队，调度将从当前工步索引恢复握手。";
});

/** 握手相位 → 四步中的第几步。4 表示全部走完；-1 表示该相位不属于任何一步（保持、人工确认、未开始）。 */
const PHASE_STEP_INDEX: Record<string, number> = {
  WaitingPlcReady: 0,
  WritingParameters: 0,
  AwaitingPlcAck: 1,
  StepRunning: 2,
  HostWait: 2,
  Completing: 3,
  ReadyToAdvance: 3,
  Completed: 4,
  Released: 4,
  DispositionRejected: 4
};

const PHASE_STEP_DEFS = [
  { key: "A", title: "A 写参", hint: "写入并回读一致" },
  { key: "B", title: "B 应答", hint: "等待 Step_Running" },
  { key: "C", title: "C 看门狗", hint: "心跳与超时监控" },
  { key: "D", title: "D 归档步进", hint: "读实测、复位、步进" }
];

const currentPhase = computed(
  () => selectedLane.value?.phase || phase.value || batch.value?.handshakePhase || ""
);

const handshakeFaulted = computed(() => currentPhase.value === "Faulted");
const activeStepIndex = computed(() => PHASE_STEP_INDEX[currentPhase.value] ?? -1);

/** 四步握手进度：已完成 / 进行中 / 未开始。相位不在映射内时全部置为未开始，由状态行说明原因。 */
const handshakeSteps = computed(() => {
  const idx = activeStepIndex.value;
  return PHASE_STEP_DEFS.map((def, i) => {
    let state: "done" | "active" | "todo" = "todo";
    if (handshakeFaulted.value) state = "todo";
    else if (idx >= PHASE_STEP_DEFS.length) state = "done";
    else if (idx >= 0) state = i < idx ? "done" : i === idx ? "active" : "todo";
    return { ...def, state };
  });
});

const handshakeStatusText = computed(() => {
  if (handshakeFaulted.value)
    return `握手故障${batch.value?.faultMessage ? ` · ${batch.value.faultMessage}` : ""}`;
  const label = handshakePhaseLabel(currentPhase.value);
  if (!label) return "尚未开始四步握手";
  const left = remainingSeconds.value != null ? ` · 剩余 ${remainingSeconds.value.toFixed(0)}s` : "";
  const which = PHASE_STEP_DEFS[activeStepIndex.value];
  return `${which ? `${which.title} · ` : ""}${label}${left}`;
});

async function load() {
  try {
    batch.value = (await http.get<BatchDetailDto>(`/batches/${route.params.id}`)).data;
    phase.value = batch.value.handshakePhase;
    lanes.value = batch.value.lanes ?? [];
    if (!selectedLaneCode.value && lanes.value[0]) selectedLaneCode.value = lanes.value[0].equipmentCode;
    if (selectedLane.value?.phase) phase.value = selectedLane.value.phase;
    const samples = (await http.get<SampleDto[]>(`/batches/${route.params.id}/samples`)).data;
    handshakeLog.value = (await http.get<HandshakeLogDto[]>(`/batches/${route.params.id}/handshake-log`)).data;
    alarms.value = (await http.get<ProcessAlarmDto[]>(`/batches/${route.params.id}/alarms`)).data;
    drifts.value = (await http.get<SnapshotDriftDto[]>(`/batches/${route.params.id}/snapshot-drift`)).data;
    equipmentIndex.value = Object.fromEntries(
      (await http.get<EquipmentDto[]>("/equipment")).data.map((e) => [e.id, e.code]));
    series.Temperature = [];
    series.Pressure = [];
    for (const s of samples) {
      if (!series[s.tag]) series[s.tag] = [];
      series[s.tag].push([new Date(s.sampledAt).getTime(), s.value]);
    }
    renderChart();
    error.value = "";
  } catch (e) {
    // 原先取数失败会留下空白页且无任何提示（根节点 v-if="batch"），这里显式报错。
    error.value = (e as Error).message || "批次详情加载失败";
  } finally {
    loading.value = false;
  }
}

function renderChart() {
  if (chartKind.value === "uplot") {
    renderUplot();
    return;
  }
  if (!chartEl.value) return;
  chart ??= echarts.init(chartEl.value);
  const entries = Object.entries(series);
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
  const names = Object.keys(series);
  const xs: number[] = [];
  const seen = new Set<number>();
  for (const pts of Object.values(series)) {
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
    const map = new Map(series[name]);
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

async function esign(title: string, needReason = false) {
  let reason: string | undefined;
  if (needReason) {
    const box = await ElMessageBox.prompt("原因 / 意见", title);
    reason = box.value;
  }
  const password = await esignPassword(title);
  return { password, reason };
}

async function start() {
  busyAction.value = "start";
  try {
    const { password } = await esign(batch.value?.status === "Faulted" ? "故障后重新排队" : "启动批次");
    await http.post(`/batches/${batch.value!.id}/start`, { password });
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    busyAction.value = "";
  }
}

async function abort() {
  busyAction.value = "abort";
  try {
    const { password, reason } = await esign("中止批次", true);
    await http.post(`/batches/${batch.value!.id}/abort`, { password, reason: reason ?? "操作员中止" });
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    busyAction.value = "";
  }
}

async function hold() {
  busyAction.value = "hold";
  try {
    const { password, reason } = await esign("保持批次（写 Host_Hold，等待 PLC_Held，禁止盲写）", true);
    await http.post(`/batches/${batch.value!.id}/hold`, { password, reason: reason ?? "操作员保持" });
    ElMessage.success("已请求保持：上位机写 Host_Hold，等待 PLC_Held 后暂停剩余时长。");
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    busyAction.value = "";
  }
}

async function resume() {
  busyAction.value = "resume";
  try {
    const { password } = await esign("恢复执行");
    await http.post(`/batches/${batch.value!.id}/resume`, { password });
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    busyAction.value = "";
  }
}

function skipTargetStepId(): string | undefined {
  const execs = batch.value?.stepExecutions ?? [];
  const live = (id?: string | null) => {
    if (!id) return undefined;
    const exec = execs.find((s) => s.stepId === id);
    return exec && ["Running", "AwaitingConfirm", "Held", "Faulted"].includes(exec.outcome) ? id : undefined;
  };
  return live(selectedLane.value?.stepId)
    ?? live(batch.value?.currentStepId)
    ?? execs.find((s) => ["Running", "AwaitingConfirm", "Held", "Faulted"].includes(s.outcome))?.stepId;
}

async function skip() {
  busyAction.value = "skip";
  try {
    const { password, reason } = await esign("跳过当前工步（仅 PLC_Ready / 等待 / 人工确认，且未写参）", true);
    await http.post(`/batches/${batch.value!.id}/skip`, {
      password,
      reason: reason ?? "主管跳步",
      stepId: skipTargetStepId()
    });
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    busyAction.value = "";
  }
}

async function ackAlarm(row: ProcessAlarmDto) {
  acking.value = true;
  try {
    await http.post(`/alarms/${row.id}/ack`);
    await load();
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    acking.value = false;
  }
}

async function confirmStep() {
  busyAction.value = "confirm";
  try {
    const currentId = selectedLane.value?.stepId || batch.value?.currentStepId;
    const { password, reason } = await esign("人工确认本工步（禁止写 PLC）", true);
    await http.post(`/batches/${batch.value!.id}/confirm`, {
      password,
      reason: reason ?? "操作员确认",
      stepId: currentId
    });
    ElMessage.success("已提交人工确认，调度将完成该工步且不写 PLC。");
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    busyAction.value = "";
  }
}

watch(chartKind, async () => {
  await nextTick();
  renderChart();
});

function onExecution(evt: ExecutionEvent) {
  if (evt.batchId !== batch.value?.id) return;
  if (evt.type === "handshake") {
    const payload = evt.payload as {
      phase: string;
      inbound: typeof signals;
      status: string;
      stepId: string;
      stepIndex: number;
      remainingSeconds?: number | null;
      equipmentCode?: string;
      unitProcedure?: string;
      stepCode?: string;
      stepName?: string;
    };
    if (payload.equipmentCode) {
      livePlc.value = payload.equipmentCode;
      if (!selectedLaneCode.value) selectedLaneCode.value = payload.equipmentCode;
      remainingByLane[payload.equipmentCode] = payload.remainingSeconds ?? null;
      if (payload.inbound)
        laneSignals[payload.equipmentCode] = { ...payload.inbound };
      const idx = lanes.value.findIndex((l) => l.equipmentCode === payload.equipmentCode);
      const next: LaneHandshakeDto = {
        equipmentCode: payload.equipmentCode,
        equipmentId: idx >= 0 ? lanes.value[idx].equipmentId : "",
        unitProcedure: payload.unitProcedure ?? (idx >= 0 ? lanes.value[idx].unitProcedure : ""),
        stepId: payload.stepId,
        stepCode: payload.stepCode ?? (idx >= 0 ? lanes.value[idx].stepCode : ""),
        phase: payload.phase,
        outcome: "Running"
      };
      if (idx >= 0) lanes.value[idx] = { ...lanes.value[idx], ...next };
      else lanes.value = [...lanes.value, next];
      if (payload.equipmentCode === selectedLaneCode.value || displayLanes.value.length <= 1) {
        phase.value = payload.phase;
        remainingSeconds.value = payload.remainingSeconds ?? null;
        Object.assign(signals, payload.inbound);
      }
    } else {
      phase.value = payload.phase;
      remainingSeconds.value = payload.remainingSeconds ?? null;
      Object.assign(signals, payload.inbound);
    }
    if (batch.value) {
      batch.value.status = payload.status as BatchDetailDto["status"];
      batch.value.currentStepIndex = payload.stepIndex;
      batch.value.currentStepId = payload.stepId;
      const exec = batch.value.stepExecutions.find((s) => s.stepId === payload.stepId);
      if (exec) {
        if (payload.status === "Held") exec.outcome = "Held";
        else if (payload.phase === "AwaitingConfirm") exec.outcome = "AwaitingConfirm";
        else if (exec.outcome === "Pending") exec.outcome = "Running";
      }
    }
  }
  if (evt.type === "step") {
    const payload = evt.payload as { stepId: string; stepIndex: number; outcome: string; qualityJson?: string };
    if (batch.value) {
      batch.value.currentStepIndex = payload.stepIndex;
      batch.value.currentStepId = payload.stepId;
      pickedStep.value = payload.stepId;
      const exec = batch.value.stepExecutions.find((s) => s.stepId === payload.stepId);
      if (exec) {
        exec.outcome = payload.outcome;
        if (payload.qualityJson) exec.qualityJson = payload.qualityJson;
      }
      const lane = lanes.value.find((l) => l.stepId === payload.stepId);
      if (lane) lane.outcome = payload.outcome;
    }
  }
  if (evt.type === "sample") {
    const measured = evt.payload as Record<string, number>;
    const t = Date.now();
    for (const [tag, value] of Object.entries(measured)) {
      series[tag] ??= [];
      series[tag].push([t, value]);
    }
    renderChart();
  }
  // 这几类事件会改变服务端状态、又不带完整数据，只能重拉；合并窗口避免一串事件打出十几个请求。
  if (evt.type === "completed" || evt.type === "fault" || evt.type === "aborted" || evt.type === "held" || evt.type === "alarm")
    scheduleReload();
}

// 本页只关心这一个批次：订 dashboard 组会把全站事件灌进这条连接。
const hub = useExecutionHub({
  onExecution,
  subscribeDashboard: false,
  subscribeBatch: () => batch.value?.id
});

onMounted(async () => {
  await load();
  // 连接起来时详情往往还没回来，subscribeBatch 取到的是 null，要靠这次补订。
  await hub.resubscribe();
  await nextTick();
  renderChart();
  poll.start();
  // uPlot 自带 autoResize，ECharts 没有：窄屏抽屉开合与手机转屏后实例仍是旧宽度。
  window.addEventListener("resize", onResize);
});

function onResize() {
  chart?.resize();
}

onUnmounted(() => {
  window.removeEventListener("resize", onResize);
  plot?.destroy();
  chart?.dispose();
  chart = null;
});
</script>

<style scoped>
.step-line { cursor: pointer; }
.muted { color: var(--muted); font-size: 12px; }
.trend-head { display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); flex-wrap: wrap; }
.up-group { margin-bottom: var(--space-3); }
.up-title { font-size: 12px; color: var(--accent-bright); margin: 0 0 var(--space-2); letter-spacing: 0.3px; }
.lane-tabs { display: flex; gap: var(--space-2); margin-bottom: var(--space-2); flex-wrap: wrap; }
.lane-tab {
  display: inline-flex; align-items: center; gap: 6px;
  padding: 3px 10px; border-radius: 999px;
  background: var(--sunken); border: 1px solid var(--line);
  color: var(--muted); font-size: 12px; cursor: pointer;
}
.lane-tab:hover { background: var(--hover); }
.lane-tab.active { border-color: var(--accent); background: var(--tint); color: var(--accent-bright); }
.signal-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(118px, 1fr)); gap: var(--space-2); }
.signal-cell {
  display: flex; align-items: center; justify-content: space-between; gap: var(--space-2);
  padding: 6px 10px; border-radius: 8px;
  background: var(--sunken); border: 1px solid var(--line);
  font-size: 12px; color: var(--muted);
}
.signal-value { color: var(--text-body); font-variant-numeric: tabular-nums; }

.handshake-steps { display: flex; align-items: flex-start; flex-wrap: wrap; }
.handshake-step { display: flex; align-items: center; gap: var(--space-2); min-width: 0; }
.step-node {
  flex: none; width: 26px; height: 26px; border-radius: 50%;
  display: inline-flex; align-items: center; justify-content: center;
  font-size: 12px; font-style: normal;
  background: var(--sunken); border: 1px solid var(--line); color: var(--muted);
}
.handshake-step.done .step-node { background: var(--tint); border-color: var(--accent); color: var(--accent-bright); }
.handshake-step.active .step-node { background: var(--accent); border-color: var(--accent-bright); color: var(--bg); }
.step-text { display: flex; flex-direction: column; min-width: 0; }
.step-text b { font-size: 12px; font-weight: 500; color: var(--text); }
.step-text span { font-size: 11px; color: var(--muted); }
.step-link { flex: none; width: 26px; height: 1px; background: var(--line); margin: 13px 4px 0; }
.step-link.done { background: var(--accent); }
.handshake-status { margin-top: var(--space-2); font-size: 12px; color: var(--text-body); }
.handshake-steps.faulted + .handshake-status { color: var(--err); }
:deep(.drift-row) { color: var(--warn); }
</style>
