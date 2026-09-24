/**
 * 握手点表 / 看门狗与表单之间的纯映射。
 *
 * 为什么单独成文件：这段是"界面上填的东西最终变成 PLC 地址"的唯一翻译点，
 * 键名要同时容忍后端持久化过的 PascalCase 与早期手写的 camelCase，16 个写参槽还要按位补齐。
 * 放在组件里它就没法被测（本仓以前没有任何前端单测），而它出错的结果是**写参落到错误偏移**。
 *
 * 解析失败一律抛，不要吞：吞掉异常再回退默认点表，等于把 DB10.x 的默认地址覆盖到现场真机上。
 */

export interface MeasuredPoint {
  name: string;
  address: string;
}

export interface TagMapForm {
  stepId: string;
  stepType: string;
  triggerWrite: string;
  plcReady: string;
  stepRunning: string;
  stepComplete: string;
  stepError: string;
  hostHold: string;
  plcHeld: string;
  errorCode: string;
  heartbeat: string;
  params: string[];
  measured: MeasuredPoint[];
  opcUaUseSecurity: boolean;
  opcUaAutoAcceptCertificates: boolean;
  opcUaUser: string;
  opcUaPassword: string;
}

export interface WatchdogForm {
  readyWaitSeconds: number;
  writeTimeoutSeconds: number;
  ackTimeoutSeconds: number;
  heartbeatTimeoutSeconds: number;
  resetTimeoutSeconds: number;
  idleSettleSeconds: number;
  holdAckSeconds: number;
}

export const PARAM_SLOTS = 16;

export const DEFAULT_WATCHDOG: WatchdogForm = {
  readyWaitSeconds: 15,
  writeTimeoutSeconds: 5,
  ackTimeoutSeconds: 8,
  heartbeatTimeoutSeconds: 3,
  resetTimeoutSeconds: 8,
  idleSettleSeconds: 10,
  holdAckSeconds: 8
};

/** 新设备的实测点起点，只是建议值：解析已存的点表时不拿它去补，见 measuredPoints。 */
export function defaultMeasured(): MeasuredPoint[] {
  return [
    { name: "Temperature", address: "DB10.84" },
    { name: "Pressure", address: "DB10.88" },
    { name: "HoldTime", address: "DB10.92" }
  ];
}

/** S7 默认布局：握手位集中在 DB10.0–DB10.16，写参从 DB10.20 起每 4 字节一槽。 */
export function emptyTagMap(): TagMapForm {
  const params: string[] = [];
  for (let i = 0; i < PARAM_SLOTS; i++) params.push(`DB10.${20 + i * 4}`);
  return {
    stepId: "DB10.0",
    stepType: "DB10.4",
    triggerWrite: "DB10.8.0",
    plcReady: "DB10.8.1",
    stepRunning: "DB10.8.2",
    stepComplete: "DB10.8.3",
    stepError: "DB10.8.4",
    hostHold: "DB10.8.5",
    plcHeld: "DB10.8.6",
    errorCode: "DB10.12",
    heartbeat: "DB10.16",
    params,
    measured: defaultMeasured(),
    opcUaUseSecurity: false,
    opcUaAutoAcceptCertificates: false,
    opcUaUser: "",
    opcUaPassword: ""
  };
}

function text(raw: Record<string, unknown>, key: string, fallback: string): string {
  const value = raw[key] ?? raw[key.charAt(0).toUpperCase() + key.slice(1)];
  return value === undefined || value === null ? fallback : String(value);
}

/**
 * 有 Measured 键就整体照抄，哪怕它一个点都没有：
 * 往上面补默认值等于给一台没有这些点的机器塞 DB10.x 幻影地址。
 */
function measuredPoints(raw: unknown): MeasuredPoint[] {
  if (raw === undefined || raw === null || typeof raw !== "object") return defaultMeasured();
  return Object.entries(raw as Record<string, unknown>)
    .filter(([, address]) => address !== undefined && address !== null)
    .map(([name, address]) => ({ name, address: String(address) }));
}

export function parseTagMap(json: string | null | undefined): TagMapForm {
  const raw = JSON.parse(json || "{}") as Record<string, unknown>;
  const next = emptyTagMap();
  next.stepId = text(raw, "stepId", next.stepId);
  next.stepType = text(raw, "stepType", next.stepType);
  next.triggerWrite = text(raw, "triggerWrite", next.triggerWrite);
  next.plcReady = text(raw, "plcReady", next.plcReady);
  next.stepRunning = text(raw, "stepRunning", next.stepRunning);
  next.stepComplete = text(raw, "stepComplete", next.stepComplete);
  next.stepError = text(raw, "stepError", next.stepError);
  next.hostHold = text(raw, "hostHold", next.hostHold);
  next.plcHeld = text(raw, "plcHeld", next.plcHeld);
  next.errorCode = text(raw, "errorCode", next.errorCode);
  next.heartbeat = text(raw, "heartbeat", next.heartbeat);

  next.measured = measuredPoints(raw.measured ?? raw.Measured);

  const params = (raw.params ?? raw.Params) as unknown[] | undefined;
  if (Array.isArray(params)) {
    for (let i = 0; i < PARAM_SLOTS; i++)
      next.params[i] = params[i] === undefined || params[i] === null ? next.params[i] : String(params[i]);
  }

  next.opcUaUseSecurity = Boolean(raw.opcUaUseSecurity ?? raw.OpcUaUseSecurity);
  next.opcUaAutoAcceptCertificates = Boolean(raw.opcUaAutoAcceptCertificates ?? raw.OpcUaAutoAcceptCertificates);
  next.opcUaUser = text(raw, "opcUaUser", "");
  next.opcUaPassword = text(raw, "opcUaPassword", "");
  return next;
}

/** 存出去一律 PascalCase（与后端 HandshakeTagMap 的属性名一致）。 */
export function serializeTagMap(map: TagMapForm): string {
  return JSON.stringify({
    StepId: map.stepId,
    StepType: map.stepType,
    TriggerWrite: map.triggerWrite,
    PlcReady: map.plcReady,
    StepRunning: map.stepRunning,
    StepComplete: map.stepComplete,
    StepError: map.stepError,
    HostHold: map.hostHold,
    PlcHeld: map.plcHeld,
    ErrorCode: map.errorCode,
    Heartbeat: map.heartbeat,
    Params: map.params,
    // 没填名称的行直接丢（字典无法用空键）；填了名称但地址空着的留下，让后端点表校验当场报错，而不是静默删掉半行。
    Measured: Object.fromEntries(
      map.measured.filter((m) => m.name.trim()).map((m) => [m.name.trim(), m.address.trim()])
    ),
    OpcUaUseSecurity: map.opcUaUseSecurity,
    OpcUaAutoAcceptCertificates: map.opcUaAutoAcceptCertificates,
    OpcUaUser: map.opcUaUser,
    OpcUaPassword: map.opcUaPassword
  });
}

export function parseWatchdog(json: string | null | undefined): WatchdogForm {
  try {
    return { ...DEFAULT_WATCHDOG, ...(JSON.parse(json || "{}") as Partial<WatchdogForm>) };
  } catch {
    // 看门狗与点表不同：它全是超时秒数，坏 JSON 回退默认值不会写错地址，只会保守一些。
    return { ...DEFAULT_WATCHDOG };
  }
}

export function serializeWatchdog(watchdog: WatchdogForm): string {
  return JSON.stringify(watchdog);
}
