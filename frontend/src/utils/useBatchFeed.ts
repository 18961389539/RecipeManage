import { computed, reactive, ref } from "vue";
import {
  getBatchAlarms, getBatchDetail, getBatchSamples, getHandshakeLog, getSnapshotDrift
} from "../api/batches";
import { listEquipment } from "../api/equipment";
import type {
  BatchDetailDto, ExecutionEvent, HandshakeLogDto, LaneHandshakeDto, ProcessAlarmDto, SnapshotDriftDto, StepOutcome
} from "../api/types";
import { emptySignals, type PlcSignals } from "./plcSignals";
import { formatHandshakeSummary, handshakePhaseToken } from "./handshake";

/**
 * 批次监控页的数据面：详情 + 四类附表 + 实时事件打补丁。
 *
 * 从页面里抽出来的理由：这 200 行是"页面上每个数字从哪来"的全部答案，
 * 混在视图里时想知道某个字段为什么没更新，得在 computed、load()、onExecution 三处之间跳。
 *
 * 视图状态（选了哪个工步、开着哪个 tab）不在这里，由页面自己持有；
 * 事件里需要动视图时通过 `onStepFocused` 回调通知。
 */
export function useBatchFeed(options: {
  batchId: () => string;
  /** 趋势图重绘钩子：series 是普通对象（不是 reactive），靠显式重画驱动。 */
  paint?: () => void;
  onStepFocused?: (stepId: string) => void;
}) {
  const batch = ref<BatchDetailDto | null>(null);
  const phase = ref("");
  const lanes = ref<LaneHandshakeDto[]>([]);
  const selectedLaneCode = ref("");
  /** 选中车道的信号。多车道时每台各存一份，单车道历史读法仍用这个。 */
  const signals = reactive<PlcSignals>(emptySignals());
  const laneSignals = reactive<Record<string, PlcSignals>>({});
  const remainingByLane = reactive<Record<string, number | null>>({});
  const remainingSeconds = ref<number | null>(null);
  const livePlc = ref("");
  const handshakeLog = ref<HandshakeLogDto[]>([]);
  const alarms = ref<ProcessAlarmDto[]>([]);
  /** 报警只取最近 200 条：total 大于它时界面要说明，不能让用户以为这一批就报过这么几次。 */
  const alarmTotal = ref(0);
  const drifts = ref<SnapshotDriftDto[]>([]);
  const equipmentIndex = ref<Record<string, string>>({});
  const series: Record<string, [number, number][]> = { Temperature: [], Pressure: [] };
  /** 趋势抽稀口径（每 step 个点取 1），step > 1 时要在图上标注。 */
  const sampleMeta = ref({ total: 0, readRows: 0, step: 1, loaded: 0 });
  const loading = ref(true);
  const error = ref("");

  /** 后端没回车道时（未启动/单设备）也要能显示一块握手位，所以这里兜一条虚拟车道。 */
  const displayLanes = computed<LaneHandshakeDto[]>(() => {
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
    }];
  });

  const selectedLane = computed(() =>
    displayLanes.value.find((l) => l.equipmentCode === selectedLaneCode.value) ?? displayLanes.value[0]);

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

  async function load() {
    const id = options.batchId();
    try {
      // 以前是六个串起来的 await：4 秒一次的轮询把首屏拖成一条瀑布，这里并行掉。
      const [detail, samples, log, alarmPage, driftRows, equipmentRows] = await Promise.all([
        getBatchDetail(id),
        getBatchSamples(id),
        getHandshakeLog(id),
        getBatchAlarms(id),
        getSnapshotDrift(id),
        listEquipment()
      ]);
      batch.value = detail;
      phase.value = detail.handshakePhase;
      lanes.value = detail.lanes ?? [];
      if (!selectedLaneCode.value && lanes.value[0]) selectedLaneCode.value = lanes.value[0].equipmentCode;
      if (selectedLane.value?.phase) phase.value = selectedLane.value.phase;
      handshakeLog.value = log;
      alarms.value = alarmPage.items;
      alarmTotal.value = alarmPage.total;
      drifts.value = driftRows;
      equipmentIndex.value = Object.fromEntries(equipmentRows.map((e) => [e.id, e.code]));

      // 整表重建：只清 Temperature/Pressure 的话，别的事件推来的测点会跨轮询越积越长（同一秒出现两次）。
      const next: Record<string, [number, number][]> = { Temperature: [], Pressure: [] };
      for (const s of samples.points) (next[s.tag] ??= []).push([new Date(s.sampledAt).getTime(), s.value]);
      for (const key of Object.keys(series)) delete series[key];
      Object.assign(series, next);
      sampleMeta.value = {
        total: samples.total,
        readRows: samples.readRows,
        step: samples.step,
        loaded: samples.points.length
      };

      error.value = "";
      options.paint?.();
    } catch (e) {
      // 原先取数失败会留下空白页且无任何提示（根节点 v-if="batch"），这里显式报错。
      error.value = (e as Error).message || "批次详情加载失败";
    } finally {
      loading.value = false;
    }
  }

  /** 把一条执行事件打到本地状态上；返回 true 表示这类事件不带完整数据、必须重拉。 */
  function applyExecution(evt: ExecutionEvent): boolean {
    if (evt.batchId !== batch.value?.id) return false;

    if (evt.type === "handshake") {
      const payload = evt.payload as {
        phase: string;
        inbound: PlcSignals;
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
        if (payload.inbound) laneSignals[payload.equipmentCode] = { ...payload.inbound };
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
        batch.value.handshakePhase = lanes.value.length > 1
          ? formatHandshakeSummary(lanes.value)
          : payload.phase;
        batch.value.currentStepIndex = payload.stepIndex;
        batch.value.currentStepId = payload.stepId;
        const exec = batch.value.stepExecutions.find((s) => s.stepId === payload.stepId);
        if (exec) {
          if (payload.status === "Held") exec.outcome = "Held";
          else if (handshakePhaseToken(payload.phase) === "AwaitingConfirm") exec.outcome = "AwaitingConfirm";
          else if (exec.outcome === "Pending") exec.outcome = "Running";
        }
      }
      return false;
    }

    if (evt.type === "step") {
      const payload = evt.payload as { stepId: string; stepIndex: number; outcome: StepOutcome; qualityJson?: string };
      if (batch.value) {
        batch.value.currentStepIndex = payload.stepIndex;
        batch.value.currentStepId = payload.stepId;
        options.onStepFocused?.(payload.stepId);
        const exec = batch.value.stepExecutions.find((s) => s.stepId === payload.stepId);
        if (exec) {
          exec.outcome = payload.outcome;
          if (payload.qualityJson) exec.qualityJson = payload.qualityJson;
        }
        const lane = lanes.value.find((l) => l.stepId === payload.stepId);
        if (lane) lane.outcome = payload.outcome;
      }
      return false;
    }

    if (evt.type === "sample") {
      const measured = evt.payload as Record<string, number>;
      const t = Date.now();
      for (const [tag, value] of Object.entries(measured)) {
        series[tag] ??= [];
        series[tag].push([t, value]);
      }
      options.paint?.();
      return false;
    }

    if (evt.type === "hold-requested" && batch.value) {
      batch.value.pendingHoldReason = (evt.payload as { reason?: string }).reason ?? "操作员保持";
      return false;
    }
    if (evt.type === "skip-requested" && batch.value) {
      batch.value.pendingSkipReason = (evt.payload as { reason?: string }).reason ?? "主管跳步";
      return false;
    }
    if (evt.type === "confirm-requested" && batch.value) {
      batch.value.pendingConfirmComment = (evt.payload as { comment?: string }).comment ?? "操作员确认";
      return false;
    }

    // 这几类事件会改变服务端状态、又不带完整数据，只能重拉；合并窗口避免一串事件打出十几个请求。
    return evt.type === "completed" || evt.type === "fault" || evt.type === "aborted" || evt.type === "held" || evt.type === "alarm";
  }

  return {
    batch, phase, lanes, selectedLaneCode, signals, remainingSeconds, livePlc,
    handshakeLog, alarms, alarmTotal, drifts, equipmentIndex, series, sampleMeta, loading, error,
    displayLanes, selectedLane, signalsOf, selectLane, load, applyExecution
  };
}
