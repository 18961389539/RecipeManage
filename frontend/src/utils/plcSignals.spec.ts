import { describe, expect, it } from "vitest";
import { emptySignals, signalCounters, signalLamps, signalsAllOff } from "./plcSignals";

/** 握手位面板的派生：7 个灯 + 2 个数值，以及"全为 0"那句解释。 */
describe("plcSignals", () => {
  it("灯的顺序与点表一致，且每盏灯都带类型", () => {
    const labels = signalLamps(emptySignals()).map((c) => c.label);
    expect(labels).toEqual([
      "PLC_Ready", "Trigger_Write", "Step_Running", "Step_Complete", "Step_Error", "Host_Hold", "PLC_Held"
    ]);
    expect(signalLamps(emptySignals()).every((c) => !!c.kind)).toBe(true);
  });

  it("Error_Code / Heartbeat 是数值，不能混进灯网", () => {
    const cells = signalCounters({ ...emptySignals(), errorCode: 17, heartbeat: 4211 });
    expect(cells.map((c) => [c.label, c.text])).toEqual([["Error_Code", "17"], ["Heartbeat", "4211"]]);
    expect(cells.every((c) => !c.kind)).toBe(true);
  });

  it("全灰才提示『信号全为 0』，只要亮一盏就不该出现这句话", () => {
    expect(signalsAllOff(emptySignals())).toBe(true);
    expect(signalsAllOff({ ...emptySignals(), stepRunning: true })).toBe(false);
    // 只有 Error_Code 有值也不算"全为 0"？算——它是数值不是灯，灯网确实全灰。
    expect(signalsAllOff({ ...emptySignals(), errorCode: 5 })).toBe(true);
  });
});
