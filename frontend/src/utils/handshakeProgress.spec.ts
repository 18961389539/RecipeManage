import { describe, expect, it } from "vitest";
import { handshakeProgress, handshakeStatusText, handshakeSummary } from "./handshakeProgress";

/**
 * 四步握手进度条是操作员判断"现在能不能写下一步"的唯一视觉依据，
 * 每一步的态都对应后端状态机里的一个相位，所以逐相位钉。
 */
const states = (phase: string) => handshakeProgress("Running", phase, phase).steps.map((s) => s.state);

describe("handshakeProgress", () => {
  it.each([
    ["WaitingPlcReady", ["active", "todo", "todo", "todo"]],
    ["WritingParameters", ["active", "todo", "todo", "todo"]],
    ["AwaitingPlcAck", ["done", "active", "todo", "todo"]],
    ["StepRunning", ["done", "done", "active", "todo"]],
    ["HostWait", ["done", "done", "active", "todo"]],
    ["Completing", ["done", "done", "done", "active"]],
    ["ReadyToAdvance", ["done", "done", "done", "active"]]
  ] as const)("相位 %s → %j", (phase, expected) => {
    expect(states(phase)).toEqual(expected);
  });

  it("保持 / 等待人工确认这类不在 A–D 映射内的相位，全部置为未开始", () => {
    expect(states("Held")).toEqual(["todo", "todo", "todo", "todo"]);
    expect(states("AwaitingConfirm")).toEqual(["todo", "todo", "todo", "todo"]);
  });

  it("故障时整条进度清零并标红，不留『走到哪了』的错觉", () => {
    const p = handshakeProgress("Faulted", "StepRunning", "StepRunning");
    expect(p.faulted).toBe(true);
    expect(p.steps.map((s) => s.state)).toEqual(["todo", "todo", "todo", "todo"]);
  });

  it("已完成 / 已放行 / 拒收都算走完四步", () => {
    for (const status of ["Completed", "Released", "DispositionRejected"]) {
      const p = handshakeProgress(status, "ReadyToAdvance", "ReadyToAdvance");
      expect(p.steps.map((s) => s.state)).toEqual(["done", "done", "done", "done"]);
    }
  });

  it("状态行文案：故障优先，其次带剩余秒数，没相位时明说尚未开始", () => {
    expect(handshakeStatusText("Faulted", "StepRunning", "StepRunning", null, "心跳丢失"))
      .toBe("握手故障 · 心跳丢失");
    expect(handshakeStatusText("Running", "StepRunning", "StepRunning", 7.4, null))
      .toContain("剩余 7s");
    expect(handshakeStatusText("Running", "", "", null, null)).toBe("尚未开始四步握手");
    expect(handshakeStatusText("Running", "StepRunning", "StepRunning", null, null))
      .toContain("C 看门狗");
  });

  it("多车道并行时副标题逐台报相位，单车道只用中文名", () => {
    const lanes = [
      { equipmentCode: "HT-01", phase: "StepRunning" },
      { equipmentCode: "MB-01", phase: "WaitingPlcReady" }
    ];
    const multi = handshakeSummary("Running", lanes, "StepRunning");
    expect(multi).toContain("HT-01:");
    expect(multi).toContain("MB-01:");
    expect(handshakeSummary("Running", [lanes[0]], "StepRunning")).not.toContain("HT-01:");
  });
});
