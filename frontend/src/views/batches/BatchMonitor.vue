<template>
  <!-- 原先根节点是 v-if="batch"：详情取数失败会留下整页空白且无任何提示。 -->
  <div class="page-state" v-if="error || (!batch && loading)">
    <el-alert
      v-if="error"
      type="error"
      show-icon
      :closable="false"
      :title="$t('批次详情加载失败：{0}', [error])"
      :description="$t('请确认该批次是否存在，或返回批次列表重试。')"
    />
    <el-skeleton v-else :rows="8" animated />
  </div>
  <div v-if="batch">
    <div class="page-title sticky-actions">
      <div>
        <h2>{{ batch.batchNo }} · {{ batch.snapshot.recipeName }} v{{ batch.snapshot.versionNumber }}<PageGuideButton guide-key="batchMonitor" /></h2>
        <div class="batch-meta">
          <span class="batch-meta-item">{{ batch.equipmentName }}</span>
          <span class="batch-meta-item">{{ batchStatusLabel(batch.status) }}</span>
          <span class="batch-meta-item">{{ summaryText }}{{ livePlcLabel }}</span>
          <span class="batch-meta-item">{{ $t("快照：{0}", [integrityLabel]) }}</span>
          <span v-if="scaleLabel" class="batch-meta-item">{{ scaleLabel }}</span>
        </div>
      </div>
      <div class="batch-actions">
        <HelpTip v-if="canConfirm && batch.status === 'Running' && awaitingConfirm" term="人工确认" chord="ctrl+enter" allow-in-input plain placement="bottom">
          <el-button type="primary" :loading="busyAction === 'confirm'" @click="confirmStep">{{ $t("人工确认") }}</el-button>
        </HelpTip>
        <HelpTip v-if="canOperate && (batch.status === 'Created' || batch.status === 'Faulted')" :term="batch.status === 'Faulted' ? '故障后重新排队' : '启动执行'" chord="ctrl+enter" allow-in-input plain placement="bottom">
          <el-button type="primary" :loading="busyAction === 'start'" @click="start">{{ $t(batch.status === 'Faulted' ? '故障后重新排队' : '启动执行') }}</el-button>
        </HelpTip>
        <HelpTip v-if="canOperate && (batch.status === 'Running' || batch.status === 'Queued')" :term="batch.pendingHoldReason ? '待保持' : '保持'" :chord="batch.pendingHoldReason ? '' : 'f8'" plain placement="bottom">
          <el-button :loading="busyAction === 'hold'" :disabled="!!batch.pendingHoldReason" @click="hold">{{ $t(batch.pendingHoldReason ? '保持已请求' : '保持') }}</el-button>
        </HelpTip>
        <HelpTip v-if="canResume && batch.status === 'Held'" term="恢复执行" chord="ctrl+enter" allow-in-input plain placement="bottom">
          <el-button type="primary" :loading="busyAction === 'resume'" @click="resume">{{ $t("恢复执行") }}</el-button>
        </HelpTip>
        <HelpTip
          v-if="canSkip && skipVisible"
          term="跳过当前工步"
          :chord="skipAllowed ? 'f9' : ''"
          :extra="skipAllowed ? '' : skipBlockedReason"
          plain
          placement="bottom"
        >
          <el-button :loading="busyAction === 'skip'" :disabled="!skipAllowed" @click="skip">{{ $t("跳过当前工步") }}</el-button>
        </HelpTip>
        <HelpTip v-if="canOperate && (batch.status === 'Running' || batch.status === 'Queued' || batch.status === 'Held')" term="中止" plain placement="bottom">
          <el-button type="danger" :loading="busyAction === 'abort'" @click="abort">{{ $t("中止") }}</el-button>
        </HelpTip>
        <el-button v-if="canTakeSample" :loading="busyAction === 'sample'" @click="takeSample">{{ $t("取样") }}</el-button>
        <el-button @click="$router.push('/batches')">{{ $t("返回批次列表") }}</el-button>
        <el-button v-if="canViewRecord" @click="$router.push(`/batches/${batch.id}/record`)">{{ $t("电子批记录") }}</el-button>
      </div>
    </div>
    <el-alert class="gap-after"
      v-if="batch.status === 'Completed'"
      :closable="false"
      type="warning"
      show-icon
      :title="$t('工艺执行与四步握手已完成。请质量在电子批记录对照归档质检后电子签名放行。')"
    />
    <el-alert class="gap-after"
      v-if="batch.status === 'Released'"
      :closable="false"
      type="success"
      show-icon
      :title="`${$t('质量已放行')}${batch.releasedBy ? ` · ${batch.releasedBy}` : ''}${batch.releaseComment ? ` · ${batch.releaseComment}` : ''}`"
    />
    <el-alert class="gap-after"
      v-if="batch.status === 'DispositionRejected'"
      :closable="false"
      type="error"
      show-icon
      :title="`${$t('质量拒收')}${batch.releaseComment ? ` · ${batch.releaseComment}` : ''}`"
    />

    <HandshakeStepsBar :steps="progress.steps" :text="statusText" :faulted="progress.faulted" />

    <div v-if="displayLanes.length > 1" class="lane-tabs gap-before">
      <button
        v-for="lane in displayLanes"
        :key="lane.equipmentCode"
        type="button"
        class="lane-tab"
        :class="{ active: (selectedLaneCode || displayLanes[0].equipmentCode) === lane.equipmentCode }"
        @click="selectLane(lane.equipmentCode)"
      >{{ lane.equipmentCode }}</button>
    </div>
    <LaneSignalsCard v-if="selectedLane" class="gap-before" :lane="selectedLane" :signals="laneSignals" />
    <el-alert class="gap-before" v-if="awaitingConfirm && batch.status === 'Running' && !batch.pendingConfirmComment" type="info" :closable="false" :title="$t('等待人工确认')" :description="$t('本工步禁止写 PLC。操作员 / 质量电子签名确认后才进入下一步。')" />
    <el-alert class="gap-before-sm" v-if="batch.status === 'Running' && batch.pendingHoldReason" type="warning" :closable="false" :title="$t('保持已请求：{0}', [batch.pendingHoldReason])" :description="$t('批次仍在运行。调度会写 Host_Hold，待 PLC_Held 后再暂停；刷新或重启不会丢掉这条已签名指令。')" />
    <el-alert class="gap-before-sm" v-if="batch.status === 'Running' && batch.pendingSkipReason" type="info" :closable="false" :title="$t('跳步已请求：{0}', [batch.pendingSkipReason])" :description="$t('等待当前工步回到可跳相位后执行，重启后仍会继续。')" />
    <el-alert class="gap-before-sm" v-if="batch.status === 'Running' && batch.pendingConfirmComment" type="info" :closable="false" :title="$t('人工确认已提交')" :description="batch.pendingConfirmComment" />
    <el-alert class="gap-before-sm" v-if="batch.status === 'Held'" type="warning" :closable="false" :title="batch.faultMessage || $t('批次保持')" :description="heldHint" />
    <el-alert class="gap-before-sm" v-else-if="batch.faultMessage" type="error" :closable="false" :title="batch.faultMessage" :description="faultHint" />
    <el-alert
      v-if="unackedAlarms.length"
      class="gap-before-sm"
      type="error"
      :closable="false"
      :title="$t('本批 {0} 条未确认过程报警', [unackedAlarms.length])"
    >
      <el-button size="small" type="danger" plain @click="monitorPane = 'alarm'">{{ $t("查看报警") }}</el-button>
    </el-alert>

    <el-tabs v-model="monitorPane" class="monitor-tabs">
      <el-tab-pane name="run">
        <template #label>
          <span class="tab-label"><HelpTip term="监控工步">{{ $t("工步") }}</HelpTip></span>
        </template>
        <el-card :header="$t('控制配方快照 · 工艺画布（冻结拓扑，禁止盲写）')">
          <p class="muted">{{ $t("与设计态同一套工艺画布。坐标按快照连线自动排布（不写入完整性哈希）。点击工步可筛选质检。") }}</p>
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

        <el-card class="gap-before" :header="$t('冻结设定矩阵（快照 vs 归档实测）')">
          <p class="muted">{{ $t("批次创建时从生效主配方冻结，执行中禁止改写。工步完成握手步骤 D 后，单元格显示归档实测并对照规格判定超差。") }}</p>
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

        <el-card class="gap-before" :header="$t('PLC 写参计划（与调度引擎同源 · 拓扑顺序）')">
          <p class="muted">{{ $t("启动前即可核对 Step_ID / Params。Wait、人工确认、质检工步禁止写 PLC；写参工步仅在 PLC_Ready 后下发并回读。") }}</p>
          <PlcWritePlan :items="batch.writePlan ?? []" :current-step-id="batch.currentStepId" />
        </el-card>

        <el-card class="gap-before" :header="$t('ISA-88 工步（按单元规程）')">
          <div v-for="group in unitGroups" :key="group.name" class="up-group">
            <div class="up-title">{{ group.name }}{{ unitEquipmentLabel(group.name) }}</div>
            <el-timeline>
              <el-timeline-item v-for="s in group.steps" :key="s.stepId" :type="timelineType(s.stepId)">
                <div class="step-line" @click="pickedStep = s.stepId">
                  {{ s.code }} {{ s.name }} · {{ stepOutcomeLabel(outcome(s.stepId)) }}
                  <div class="muted">{{ phaseTypeLabel(s) }}</div>
                </div>
              </el-timeline-item>
            </el-timeline>
          </div>
          <el-divider />
          <div v-if="qualityRows.length">
            <b>{{ $t("工步归档质检") }}</b>
            <el-table class="gap-before-sm" :data="qualityRows" size="small">
              <el-table-column prop="step" :label="$t('工步')" width="70" fixed />
              <el-table-column :label="$t('测点')">
                <template #default="{ row }">{{ signalLabel(row.tag) }}</template>
              </el-table-column>
              <el-table-column prop="value" :label="$t('实测')" />
              <el-table-column prop="spec" :label="$t('规格')" width="90">
                <template #default="{ row }">
                  <span :style="{ color: row.oos ? 'var(--err)' : 'var(--ok)' }">{{ row.spec }}</span>
                </template>
              </el-table-column>
            </el-table>
          </div>
          <div v-else class="muted">{{ $t("完成握手步骤 D 后，此处显示该工步归档实测。点击时间线工步可筛选。") }}</div>
        </el-card>
      </el-tab-pane>

      <el-tab-pane name="trend">
        <template #label>
          <span class="tab-label"><HelpTip term="实时工艺趋势">{{ $t("趋势") }}</HelpTip></span>
        </template>
        <BatchTrendCharts ref="charts" :series="series" :active="monitorPane === 'trend'" :note="trendNote" />
      </el-tab-pane>

      <el-tab-pane name="alarm">
        <template #label>
          <span class="tab-label">
            <HelpTip term="过程报警">{{ $t("报警") }}</HelpTip>
            <el-tag v-if="unackedAlarms.length" type="danger" size="small">{{ unackedAlarms.length }}</el-tag>
          </span>
        </template>
        <el-card>
          <template #header>
            <div class="trend-head">
              <span>{{ $t("过程报警") }}</span>
              <el-button
                v-if="unackedAlarms.length && canAckAlarm"
                size="small"
                :loading="ackingId === 'bulk'"
                :disabled="!!ackingId"
                @click="ackAllAlarms"
              >{{ $t("确认全部 {0} 条", [unackedAlarms.length]) }}</el-button>
            </div>
          </template>
          <el-table v-if="alarms.length" :data="alarms" size="small">
            <el-table-column :label="$t('时间')" width="170" fixed>
              <template #default="{ row }">{{ formatDateTime(row.raisedAt) }}</template>
            </el-table-column>
            <el-table-column prop="stepCode" :label="$t('工步')" width="70" />
            <el-table-column prop="code" :label="$t('代码')" width="120" />
            <el-table-column prop="message" :label="$t('说明')" />
            <el-table-column :label="$t('确认')" width="100">
              <template #default="{ row }">
                <el-button v-if="!row.acknowledgedAt && canAckAlarm" link type="primary" :loading="ackingId === row.id" :disabled="!!ackingId" @click="ackAlarm(row)">{{ $t("确认") }}</el-button>
                <span v-else>{{ row.acknowledgedBy || "—" }}</span>
              </template>
            </el-table-column>
          </el-table>
          <p v-else class="none-note">{{ $t("本批无过程报警。") }}</p>
          <p v-if="alarmTotal > alarms.length" class="none-note">
            {{ $t("仅显示最近 {0} 条，本批共 {1} 条报警；「确认全部」只作用于上面列出的这些。", [alarms.length, alarmTotal]) }}
          </p>
        </el-card>
      </el-tab-pane>

      <el-tab-pane name="log">
        <template #label>
          <span class="tab-label"><HelpTip term="握手履历">{{ $t("履历") }}</HelpTip></span>
        </template>
        <el-row :gutter="12">
          <el-col :span="14" :xs="24">
            <el-card :header="$t('握手时序（禁止盲写）')">
              <el-table :data="handshakeLog" size="small" max-height="360">
                <el-table-column prop="stepCode" :label="$t('工步')" width="70" fixed />
                <el-table-column :label="$t('时间')" width="170">
                  <template #default="{ row }">{{ formatDateTime(row.at) }}</template>
                </el-table-column>
                <el-table-column :label="$t('阶段')" width="130">
                  <template #default="{ row }">{{ handshakePhaseLabel(row.phase) }}</template>
                </el-table-column>
                <el-table-column :label="$t('动作')" width="80">
                  <template #default="{ row }">{{ handshakeKindLabel(row.kind) }}</template>
                </el-table-column>
                <el-table-column prop="detail" :label="$t('说明')" />
                <el-table-column prop="remainingSeconds" :label="$t('剩余s')" width="70">
                  <template #default="{ row }">{{ row.remainingSeconds == null ? "—" : Number(row.remainingSeconds).toFixed(0) }}</template>
                </el-table-column>
              </el-table>
              <div v-if="!handshakeLog.length" class="muted">{{ $t("启动批次后，此处按 PLC_Ready → Trigger_Write → Step_Running → Step_Complete 记录每一次合法动作。") }}</div>
              <div v-else-if="handshakeLogTotal > handshakeLog.length" class="muted">
                {{ $t("仅显示最近 {0} 条，本批共 {1} 条握手事件；完整履历见电子批记录。", [handshakeLog.length, handshakeLogTotal]) }}
              </div>
            </el-card>
          </el-col>
          <el-col :span="10" :xs="24">
            <el-card :header="$t('快照 vs 当前生效主配方')">
              <el-alert class="gap-after-sm"
                v-if="hasDrift"
                :closable="false"
                type="warning"
                show-icon
                :title="$t('控制配方快照已冻结。主配方升版只体现在漂移列，禁止改写本批次写参。')"
              />
              <el-table :data="drifts" size="small" max-height="360" :row-class-name="driftRowClass">
                <el-table-column prop="stepCode" :label="$t('工步')" width="70" fixed />
                <el-table-column prop="parameter" :label="$t('参数')" />
                <el-table-column prop="frozenSetpoint" :label="$t('快照')" width="80" />
                <el-table-column prop="masterSetpoint" :label="$t('主配方')" width="80" />
                <el-table-column :label="$t('漂移')" width="70">
                  <template #default="{ row }">{{ row.drifted ? "是" : "否" }}</template>
                </el-table-column>
              </el-table>
            </el-card>
          </el-col>
        </el-row>
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { t } from "../../i18n";
import { computed, onMounted, ref } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessage, ElMessageBox } from "element-plus";
import http from "../../api/http";
import { acknowledgeAlarm, esignBatchAction } from "../../api/batches";
import type { ExecutionEvent, ProcessAlarmDto, SnapshotDriftDto } from "../../api/types";
import { useAuthStore } from "../../stores/auth";
import { useAlarmBadgeStore } from "../../stores/alarms";
import { useExecutionHub } from "../../realtime/executionHub";
import ProcedureFlow from "../../components/ProcedureFlow.vue";
import SetpointMatrix from "../../components/SetpointMatrix.vue";
import PlcWritePlan from "../../components/PlcWritePlan.vue";
import HandshakeStepsBar from "../../components/HandshakeStepsBar.vue";
import LaneSignalsCard from "../../components/LaneSignalsCard.vue";
import BatchTrendCharts from "../../components/BatchTrendCharts.vue";
import { useBatchFeed } from "../../utils/useBatchFeed";
import { useBatchSnapshotView } from "../../utils/useBatchSnapshotView";
import { handshakeProgress, handshakeStatusText, handshakeSummary } from "../../utils/handshakeProgress";
import { handshakeDisplayPhase, handshakePhaseToken } from "../../utils/handshake";
import { handshakePhaseLabel, handshakeKindLabel, batchStatusLabel, esignMeaning, phaseTypeLabel, signalLabel, stepOutcomeLabel } from "../../utils/labels";
import { formatDateTime } from "../../utils/format";
import { esignPassword, esignWithReason } from "../../utils/esign";
import { useCoalescedReload } from "../../utils/useCoalescedReload";
import { usePolling } from "../../utils/usePolling";
import HelpTip from "../../components/HelpTip.vue";
import { usePageShortcuts } from "../../shortcuts/registry";

/**
 * 批次实时监控。
 *
 * 数据面在 utils/useBatchFeed（详情 + 附表 + 实时事件打补丁），快照投影在 utils/useBatchSnapshotView，
 * 四步进度在 utils/handshakeProgress，握手位在 utils/plcSignals；本页只剩"看哪个车道/工步"、
 * 能不能按这个按钮，以及六个电子签名动作。
 */
const route = useRoute();
const router = useRouter();
const auth = useAuthStore();
const alarmBadge = useAlarmBadgeStore();
const pickedStep = ref<string | null>(null);
const charts = ref<InstanceType<typeof BatchTrendCharts> | null>(null);

const feed = useBatchFeed({
  batchId: () => String(route.params.id ?? ""),
  paint: () => charts.value?.refresh(),
  onStepFocused: (stepId) => (pickedStep.value = stepId)
});
const alarms = feed.alarms;
const drifts = feed.drifts;
const handshakeLog = feed.handshakeLog;
const handshakeLogTotal = feed.handshakeLogTotal;
const displayLanes = feed.displayLanes;
const selectedLane = feed.selectedLane;
const view = useBatchSnapshotView(feed.batch, pickedStep, feed.equipmentIndex);

/**
 * 解构只是为了让模板能读到解包后的值：Vue 只会自动解包 setup 顶层绑定，
 * `feed.error.value` / `view.integrityLabel.value` 这种写法留在模板里既难读也容易被改成漏掉 .value。
 */
const { batch, phase, loading, error, livePlc, remainingSeconds, selectedLaneCode, series, sampleMeta, alarmTotal, selectLane, signalsOf, load, applyExecution } = feed;
const {
  outcome, timelineType, flowSteps, flowEdges, flowOutcomes, matrixSteps, measured,
  unitGroups, qualityRows, integrityLabel, scaleLabel, unitEquipmentLabel
} = view;

/**
 * 取数口径要写在图上：长批次的样本量远超画布能承载的点数，后端按步长在 SQL 侧取样，
 * 覆盖整批但只回千把点。不标注的话用户会拿取样曲线当全量履历读——批记录用的仍是全量数据。
 */
const trendNote = computed(() => {
  const { total, step } = sampleMeta.value;
  if (!total) return "";
  if (step > 1)
    return t("趋势覆盖整批，每 {0} 条样本取 1 点绘出（本批共 {1} 条）。样本不删除，电子批记录仍用全量数据。", step, total);
  return t("共 {0} 条样本，全部绘出。", total);
});

const laneSignals = computed(() => signalsOf(selectedLane.value?.equipmentCode ?? ""));

type MonitorPane = "run" | "trend" | "alarm" | "log";
const monitorPanes = new Set<MonitorPane>(["run", "trend", "alarm", "log"]);
const monitorPane = computed<MonitorPane>({
  get: () => {
    const raw = route.query.tab;
    const value = Array.isArray(raw) ? raw[0] : raw;
    const key = typeof value === "string" ? value : "";
    return monitorPanes.has(key as MonitorPane) ? (key as MonitorPane) : "run";
  },
  set: (value) => {
    const next = { ...route.query };
    if (value === "run") delete next.tab;
    else next.tab = value;
    void router.replace({ query: next });
  }
});

/** 操作按钮防重入：任一签名+请求在途时，对应按钮显示 loading 且禁用，避免重复提交。 */
const busyAction = ref<string>("");
const ackingId = ref("");

/** 当前相位：保持/故障/完成态优先，其次看选中车道，最后回落到批次汇总串。 */
const currentPhase = computed(() =>
  handshakePhaseToken(handshakeDisplayPhase(batch.value?.status, selectedLane.value?.phase, phase.value))
);
const progress = computed(() =>
  handshakeProgress(batch.value?.status, selectedLane.value?.phase, phase.value || batch.value?.handshakePhase)
);
const statusText = computed(() =>
  handshakeStatusText(
    batch.value?.status,
    selectedLane.value?.phase,
    phase.value || batch.value?.handshakePhase,
    remainingSeconds.value,
    batch.value?.faultMessage
  )
);
const summaryText = computed(() =>
  handshakeSummary(batch.value?.status, displayLanes.value, currentPhase.value)
);
const livePlcLabel = computed(() => (livePlc.value ? ` · ${livePlc.value}` : ""));

const skipReady = computed(() => {
  const p = currentPhase.value;
  return p === "WaitingPlcReady" || p === "Held" || p === "AwaitingConfirm" || p === "HostWait";
});
const skipVisible = computed(() => {
  const st = batch.value?.status;
  return st === "Held" || st === "Faulted" || st === "Running";
});
const skipAllowed = computed(() => {
  if (!batch.value) return false;
  if (batch.value.status === "Held" || batch.value.status === "Faulted") return true;
  return batch.value.status === "Running" && skipReady.value;
});
const skipBlockedReason = computed(() =>
  t("当前握手阶段不可跳过：等到等待 PLC 就绪、保持、人工确认或主控等待后再试。")
);

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
    return t("归档实测超出规格，已保持批次且禁止写下一步。质量/主管审核后恢复执行。");
  const p = currentPhase.value;
  if (p === "AwaitingConfirm" || awaitingConfirm.value)
    return t("人工确认等待中被保持，未写 PLC。恢复后继续等待电子签名确认。");
  return t("已写 Host_Hold 并收到 PLC_Held。恢复执行将清位并继续剩余工步时长，禁止盲写下一步。");
});

const canOperate = computed(() => auth.can("Operator", "Supervisor"));
const canSkip = computed(() => auth.can("Supervisor"));
const canConfirm = computed(() => auth.can("Operator", "Supervisor", "Quality"));
const canResume = computed(() => auth.can("Operator", "Supervisor"));
const canAckAlarm = computed(() => auth.can("Operator", "Supervisor", "Quality"));
// 批记录（归档凭据）按角色收敛：主管 / 质量 / 管理员；操作员在监控页看实时执行。
const canViewRecord = computed(() => auth.can("Admin", "Supervisor", "Quality"));
// 取样（lims 登记）与批记录查看是两件事：操作员保留取样入口——归档件不再对车间开放后，
// 监控页的取样按钮就是 lot.handle 里"登记样品"在界面上的落点。
const canTakeSample = computed(() => auth.can("Operator", "Quality", "Supervisor"));
const unackedAlarms = computed(() => alarms.value.filter((a) => !a.acknowledgedAt));
const hasDrift = computed(() => drifts.value.some((d) => d.drifted));
function driftRowClass({ row }: { row: SnapshotDriftDto }) {
  return row.drifted ? "drift-row" : "";
}

const faultHint = computed(() => {
  const code = batch.value?.faultCode ?? "";
  const map: Record<string, string> = {
    PlcReadyTimeout: t("等待 PLC_Ready 超时：检查握手位与通讯，确认 PLC 空闲后再重新排队。"),
    HeartbeatLost: t("工步执行中心跳丢失：检查看门狗时间与现场心跳程序。"),
    ExecutionTimeout: t("工步看门狗超时：核对设定值与 PLC 程序，必要时延长该工步看门狗。"),
    AckTimeout: t("Trigger_Write 后未收到 Step_Running：禁止盲写下一步，先复位握手位。"),
    PlcReportedError: t("PLC 报 Step_Error：读取 Error_Code 后按设备手册处理。"),
    BlindWriteRejected: t("状态机拒绝跨阶段写参，保持四步握手顺序。"),
    WriteVerifyMismatch: t("写参回读与快照不一致：禁止 Trigger_Write，检查点表与 PLC 程序后再重新排队。"),
    HoldAckTimeout: t("Host_Hold 后未收到 PLC_Held：禁止盲写下一步，检查保持握手位。"),
    PlcCommLost: t("读 PLC 持续失败，超出容忍窗口：先到现场确认设备实际状态（PLC 可能仍在按程序运行），恢复通讯后再重新排队。")
  };
  return map[code] ?? t("可在故障清除后重新排队，调度将从当前工步索引恢复握手。");
});

async function esign(title: string, action: string, needReason = false) {
  if (!needReason) return { password: await esignPassword(title, esignMeaning(action)), reason: undefined };
  const { reason, password } = await esignWithReason(title, esignMeaning(action), "原因 / 意见");
  return { password, reason };
}

/**
 * 六个签名动作共用的外壳：置忙 → 签名+提交 → 重拉 → 复位。
 *
 * 取消签名时 esign 抛的是字符串 "cancel"，不是 Error——那不算失败，不能弹红条。
 * 成功后的重拉放在这里，六个动作就不各自记得补一次 load()。
 */
async function runAction(name: string, action: (id: string) => Promise<void>) {
  const id = batch.value?.id;
  if (!id) return;
  busyAction.value = name;
  try {
    await action(id);
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    busyAction.value = "";
  }
}

function start() {
  const retry = batch.value?.status === "Faulted";
  return runAction("start", async (id) => {
    const { password } = await esign(
      retry ? "故障后重新排队" : "启动批次",
      retry ? "batch.retry.esign" : "batch.start.esign"
    );
    await esignBatchAction(id, "start", { password });
  });
}

function abort() {
  return runAction("abort", async (id) => {
    const { password, reason } = await esign("中止批次", "batch.abort.esign", true);
    await esignBatchAction(id, "abort", { password, reason: reason ?? "操作员中止" });
  });
}

function hold() {
  return runAction("hold", async (id) => {
    const { password, reason } = await esign("保持批次（写 Host_Hold，等待 PLC_Held，禁止盲写）", "batch.hold.esign", true);
    await esignBatchAction(id, "hold", { password, reason: reason ?? "操作员保持" });
    ElMessage.success(t("已请求保持：上位机写 Host_Hold，等待 PLC_Held 后暂停剩余时长。"));
  });
}

function resume() {
  return runAction("resume", async (id) => {
    const { password } = await esign("恢复执行", "batch.resume.esign");
    await esignBatchAction(id, "resume", { password });
  });
}

/**
 * 取样走监控页而不是批记录页：批记录（归档凭据）不再对车间开放后，
 * `lot.handle` 里"登记样品"在界面上的落点就是这里。
 * 不传物料批：服务端默认绑本批产出批（谱系链接不断），监控页也拿不到产出批 id。
 */
function takeSample() {
  return runAction("sample", async (id) => {
    const { value: code } = await ElMessageBox.prompt(t("样品编号"), t("实验室取样"));
    await http.post(`/batches/${id}/lab-samples`, { sampleCode: code, sampleType: "Final" });
    ElMessage.success(t("已登记样品"));
  });
}

/** 跳步/确认都要落到"这一条车道当前这一步"，否则会跳到隔壁车道的工步上。 */
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

function skip() {
  if (!skipAllowed.value) return Promise.resolve();
  return runAction("skip", async (id) => {
    const { password, reason } = await esign("跳过当前工步（仅 PLC_Ready / 等待 / 人工确认，且未写参）", "batch.skip.esign", true);
    await esignBatchAction(id, "skip", { password, reason: reason ?? "主管跳步", stepId: skipTargetStepId() });
  });
}

function confirmStep() {
  return runAction("confirm", async (id) => {
    const currentId = selectedLane.value?.stepId || batch.value?.currentStepId;
    const { password, reason } = await esign("人工确认本工步（禁止写 PLC）", "batch.confirm.esign", true);
    await esignBatchAction(id, "confirm", { password, reason: reason ?? "操作员确认", stepId: currentId });
    ElMessage.success(t("已提交人工确认，调度将完成该工步且不写 PLC。"));
  });
}

async function ackAlarm(row: ProcessAlarmDto) {
  if (ackingId.value) return;
  ackingId.value = row.id;
  try {
    await acknowledgeAlarm(row.id);
    await load();
    // 侧栏徽标跟着减：确认完数字还挂着会像没生效。
    void alarmBadge.refresh();
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    ackingId.value = "";
  }
}

async function ackAllAlarms() {
  if (ackingId.value) return;
  const pending = unackedAlarms.value;
  if (!pending.length) return;
  try {
    await ElMessageBox.confirm(
      t("确认本批次 {0} 条未确认报警？确认后仍保留履历。", pending.length),
      t("批量确认报警"),
      { type: "warning", confirmButtonText: t("全部确认"), cancelButtonText: t("取消") }
    );
  } catch {
    return;
  }
  ackingId.value = "bulk";
  let ok = 0;
  try {
    for (const row of pending) {
      await acknowledgeAlarm(row.id);
      ok += 1;
    }
    await load();
    ElMessage.success(t("已确认 {0} 条报警", ok));
  } catch (e) {
    await load();
    ElMessage.error(t("已确认 {0} 条，其余失败：{1}", ok, (e as Error).message));
  } finally {
    ackingId.value = "";
    // 成功与部分失败都刷新：批量里已经确认掉的那几条要让侧栏徽标立刻反映。
    void alarmBadge.refresh();
  }
}

usePageShortcuts(() => [
  {
    id: "batch.primary",
    chord: "ctrl+enter",
    group: t("批次监控"),
    label: awaitingConfirm.value
      ? t("人工确认")
      : batch.value?.status === "Held"
        ? t("恢复执行")
        : t("启动执行"),
    allowInInput: true,
    when: () => {
      if (!batch.value || busyAction.value) return false;
      if (canConfirm.value && batch.value.status === "Running" && awaitingConfirm.value) return true;
      if (canOperate.value && (batch.value.status === "Created" || batch.value.status === "Faulted")) return true;
      if (canResume.value && batch.value.status === "Held") return true;
      return false;
    },
    run: () => {
      if (!batch.value) return;
      if (canConfirm.value && batch.value.status === "Running" && awaitingConfirm.value) void confirmStep();
      else if (canOperate.value && (batch.value.status === "Created" || batch.value.status === "Faulted")) void start();
      else if (canResume.value && batch.value.status === "Held") void resume();
    }
  },
  {
    id: "batch.hold",
    chord: "f8",
    group: t("批次监控"),
    label: t("保持"),
    when: () =>
      !!batch.value
      && canOperate.value
      && (batch.value.status === "Running" || batch.value.status === "Queued")
      && !batch.value.pendingHoldReason
      && !busyAction.value,
    run: () => { void hold(); }
  },
  {
    id: "batch.skip",
    chord: "f9",
    group: t("批次监控"),
    label: t("跳过当前工步"),
    when: () =>
      !!batch.value
      && canSkip.value
      && skipAllowed.value
      && !busyAction.value,
    run: () => { void skip(); }
  }
]);

// 事件与兜底轮询都走这条：合并窗口 + 在途去重，避免一串握手事件把详情接口打满。
const scheduleReload = useCoalescedReload(load);
const poll = usePolling(scheduleReload);

// 本页只关心这一个批次：订 dashboard 组会把全站事件灌进这条连接。
const hub = useExecutionHub({
  onExecution: (evt: ExecutionEvent) => { if (applyExecution(evt)) scheduleReload(); },
  subscribeDashboard: false,
  subscribeBatch: () => batch.value?.id
});

onMounted(async () => {
  await load();
  // 连接起来时详情往往还没回来，subscribeBatch 取到的是 null，要靠这次补订。
  await hub.resubscribe();
  poll.start();
});
</script>

<style scoped>
.step-line { cursor: pointer; }
.muted { color: var(--muted); font-size: 12px; }
.batch-meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 0;
  margin-top: 4px;
}
.batch-meta-item {
  display: inline-flex;
  align-items: center;
  min-width: 0;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.45;
  overflow-wrap: anywhere;
}
.batch-meta-item + .batch-meta-item::before {
  content: "";
  width: 3px;
  height: 3px;
  flex: none;
  margin: 0 8px;
  border-radius: 50%;
  background: var(--muted);
}
.batch-actions { gap: var(--space-2); }
.batch-actions :deep(.el-button) { min-height: 38px; }
.trend-head { display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); flex-wrap: wrap; }
.monitor-tabs { margin-top: var(--space-3); }
.monitor-tabs :deep(.el-tabs__header) { margin-bottom: var(--space-3); }
.monitor-tabs :deep(.el-tabs__item) { padding: 0 16px; }
.tab-label { display: inline-flex; align-items: center; gap: 6px; }
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
:deep(.drift-row) { color: var(--warn); }
@media (max-width: 768px) {
  .batch-actions {
    width: 100%;
    flex: 1 1 100%;
    justify-content: flex-start;
  }
  .batch-actions :deep(.el-button) { min-height: 40px; }
  .batch-meta { row-gap: 2px; }
}
</style>
