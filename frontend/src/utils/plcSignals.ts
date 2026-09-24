/** PLC 握手位：驱动读回来的原始信号，以及"7 个灯 + 2 个计数"的展示派生。 */

export interface PlcSignals {
  plcReady: boolean;
  stepRunning: boolean;
  stepComplete: boolean;
  stepError: boolean;
  errorCode: number;
  heartbeat: number;
  triggerWriteEcho: boolean;
  plcHeld: boolean;
  hostHoldEcho: boolean;
}

export function emptySignals(): PlcSignals {
  return {
    plcReady: false, stepRunning: false, stepComplete: false, stepError: false,
    errorCode: 0, heartbeat: 0, triggerWriteEcho: false, plcHeld: false, hostHoldEcho: false
  };
}

export interface SignalCell {
  label: string;
  /** 灯的类型；缺省表示这是一格纯数值（Error_Code / Heartbeat）。 */
  kind?: "on" | "run" | "err";
  on?: boolean;
  text?: string;
}

/** 7 个握手灯，按网格铺开，避免 8 行竖排把卡片拉长。 */
export function signalLamps(s: PlcSignals): SignalCell[] {
  return [
    { label: "PLC_Ready", kind: "on", on: !!s.plcReady },
    { label: "Trigger_Write", kind: "run", on: !!s.triggerWriteEcho },
    { label: "Step_Running", kind: "run", on: !!s.stepRunning },
    { label: "Step_Complete", kind: "on", on: !!s.stepComplete },
    { label: "Step_Error", kind: "err", on: !!s.stepError },
    { label: "Host_Hold", kind: "run", on: !!s.hostHoldEcho },
    { label: "PLC_Held", kind: "on", on: !!s.plcHeld }
  ];
}

/** Error_Code / Heartbeat 是数值不是灯，混在灯网里会读成"两个灰灯"。 */
export function signalCounters(s: PlcSignals): SignalCell[] {
  return [
    { label: "Error_Code", text: s.errorCode == null ? "—" : String(s.errorCode) },
    { label: "Heartbeat", text: s.heartbeat == null ? "—" : String(s.heartbeat) }
  ];
}

/** 全灰的一排灯不解释就是"界面坏了"，得配一句整体态。 */
export function signalsAllOff(s: PlcSignals): boolean {
  return signalLamps(s).every((c) => !c.on);
}
