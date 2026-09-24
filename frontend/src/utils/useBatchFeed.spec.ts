import { describe, expect, it, vi } from "vitest";
import type { BatchDetailDto, ExecutionEvent } from "../api/types";
import { useBatchFeed } from "./useBatchFeed";

/**
 * 实时事件打补丁的那半个 feed。
 *
 * 这里测的是"事件到达后屏幕上那个数字该变成什么"，不测网络：
 * load() 才走接口，而 applyExecution 是纯状态迁移，可以脱离 hub 与 DOM 断言。
 */
function makeBatch(over: Partial<BatchDetailDto> = {}): BatchDetailDto {
  return {
    id: "b1",
    batchNo: "B1",
    status: "Running",
    handshakePhase: "StepRunning",
    equipmentId: "e1",
    equipmentName: "HT-01",
    currentStepId: "s10",
    currentStepIndex: 0,
    stepExecutions: [
      { stepId: "s10", stepCode: "S10", stepName: "升温", stepType: "Heat", ordinal: 0, outcome: "Running" },
      { stepId: "s20", stepCode: "S20", stepName: "保温", stepType: "Hold", ordinal: 1, outcome: "Pending" }
    ],
    lanes: [],
    snapshot: { steps: [], edges: [], scaleFactor: 1 },
    snapshotIntegrity: "Valid",
    writePlan: [],
    ...over
  } as unknown as BatchDetailDto;
}

const evt = (type: string, payload: unknown, batchId = "b1"): ExecutionEvent =>
  ({ type, payload, batchId }) as ExecutionEvent;

describe("useBatchFeed.applyExecution", () => {
  it("不是本页批次的事件直接忽略", () => {
    const feed = useBatchFeed({ batchId: () => "b1" });
    feed.batch.value = makeBatch();
    expect(feed.applyExecution(evt("handshake", { phase: "Faulted" }, "other"))).toBe(false);
    expect(feed.phase.value).toBe("");
  });

  it("握手事件更新相位、信号与批次状态，并且不要求重拉", () => {
    const feed = useBatchFeed({ batchId: () => "b1" });
    feed.batch.value = makeBatch();
    const needs = feed.applyExecution(evt("handshake", {
      phase: "StepRunning", status: "Running", stepId: "s10", stepIndex: 0, remainingSeconds: 12,
      equipmentCode: "HT-01", inbound: { plcReady: true, stepRunning: true, errorCode: 0, heartbeat: 7 }
    }));
    expect(needs).toBe(false);
    expect(feed.phase.value).toBe("StepRunning");
    expect(feed.signals.plcReady).toBe(true);
    expect(feed.remainingSeconds.value).toBe(12);
    expect(feed.livePlc.value).toBe("HT-01");
    // 事件自带车道信息，不必为了刷新一个灯去重拉详情
    expect(feed.displayLanes.value.map((l) => l.equipmentCode)).toEqual(["HT-01"]);
  });

  it("车道相位汇总：两台以上才拼 CODE:Phase 串", () => {
    const feed = useBatchFeed({ batchId: () => "b1" });
    feed.batch.value = makeBatch();
    feed.applyExecution(evt("handshake", { phase: "StepRunning", status: "Running", stepId: "s10", stepIndex: 0, equipmentCode: "HT-01", inbound: {} }));
    feed.applyExecution(evt("handshake", { phase: "WaitingPlcReady", status: "Running", stepId: "s20", stepIndex: 1, equipmentCode: "MB-01", inbound: {} }));
    expect(feed.lanes.value).toHaveLength(2);
    expect(feed.batch.value?.handshakePhase).toBe("HT-01:StepRunning · MB-01:WaitingPlcReady");
  });

  it("工步事件推进归档结果并通知页面聚焦，Held 事件把执行态改出来", () => {
    const focused: string[] = [];
    const feed = useBatchFeed({ batchId: () => "b1", onStepFocused: (id) => focused.push(id) });
    feed.batch.value = makeBatch();
    expect(feed.applyExecution(evt("step", { stepId: "s10", stepIndex: 1, outcome: "Completed", qualityJson: "{}" }))).toBe(false);
    expect(focused).toEqual(["s10"]);
    expect(feed.batch.value?.stepExecutions[0].outcome).toBe("Completed");
    feed.applyExecution(evt("handshake", { phase: "HostWait", status: "Held", stepId: "s20", stepIndex: 1, inbound: {} }));
    expect(feed.batch.value?.stepExecutions[1].outcome).toBe("Held");
  });

  it("采样点并进 series 并请求重绘；完成/故障/报警类事件要求重拉", () => {
    const paint = vi.fn();
    const feed = useBatchFeed({ batchId: () => "b1", paint });
    feed.batch.value = makeBatch();
    feed.applyExecution(evt("sample", { Temperature: 528.5, Pressure: 1.02 }));
    expect(feed.series.Temperature.at(-1)?.[1]).toBe(528.5);
    expect(feed.series.Pressure.at(-1)?.[1]).toBe(1.02);
    expect(paint).toHaveBeenCalledTimes(1);
    for (const type of ["completed", "fault", "aborted", "held", "alarm"])
      expect(feed.applyExecution(evt(type, {}))).toBe(true);
    for (const type of ["hold-requested", "skip-requested", "confirm-requested"]) {
      expect(feed.applyExecution(evt(type, { reason: "现场换料" }))).toBe(false);
    }
    expect(feed.batch.value?.pendingHoldReason).toBe("现场换料");
  });
});
