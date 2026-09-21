/**
 * 领域术语 glossary。
 *
 * 背景：本系统术语密集（四步握手、控制配方快照、完整性哈希、禁止盲写、ISA-88 层级……），
 * 原先解释文案散落在 el-alert 横幅、placeholder 和原生 title 属性里，且全应用没有一个 el-tooltip，
 * 操作员在看到「等待 PLC 应答」这类状态时没有任何可发现的解释入口。
 * 此处作为解释文案的唯一数据源，由 components/HelpTip.vue 消费。
 *
 * key 一律使用界面上实际渲染的中文词（与 utils/labels.ts 的中文化结果保持一致），方便直接按词查。
 */
export interface TermEntry {
  /** 一句话定义 */
  readonly text: string;
  /** 可选的补充/约束说明 */
  readonly detail?: string;
}

const D: Record<string, TermEntry> = {
  "控制配方快照": {
    text: "批次启动时把当时的主配方版本冻结成一份只读副本，批次执行期间即使主配方升版也仍按快照执行。",
    detail: "冻结后写入 frozenAt 并计算完整性哈希，用于事后证明「这批当时就是按这套参数跑的」。"
  },
  "完整性哈希": {
    text: "对冻结的控制配方内容（工步、参数设定值等）计算的校验值，任何事后改动都会导致哈希不符。",
    detail: "物料批号不纳入哈希——它属于产出标识，不是工艺参数。"
  },
  "四步握手": {
    text: "主控与 PLC 之间固定的四步写参握手：等就绪 → 下参数 → 等应答 → 收尾推进。",
    detail: "任何一步超时或回读不一致都会判为握手故障，宁可停批也不改写设备。"
  },
  "等待 PLC 就绪": {
    text: "主控已选定目标工步，正在等 PLC 把 PLC_Ready 置位，表示设备可以接收参数。",
    detail: "未就绪期间不会写入任何参数（禁止盲写）。"
  },
  "下发参数": {
    text: "正在把本工步的参数写入 PLC 的参数区。",
    detail: "写入后会回读比对，用于检出地址错误或数据被覆盖。"
  },
  "等待 PLC 应答": {
    text: "已置位 Trigger_Write 通知 PLC 取参，正在等 PLC 给出 Trigger_Ack 应答。",
    detail: "超时未应答会判为握手故障并产生报警。"
  },
  "工步执行中": {
    text: "PLC 已确认参数并开始执行本工步（如升温、保温）。",
    detail: "此阶段主控只监视 Step_Running / Step_Complete / Step_Error 与心跳。"
  },
  "收尾确认": {
    text: "PLC 报告 Step_Complete，主控正在确认本工步正常结束。",
    detail: "确认通过后才允许推进到下一工步。"
  },
  "可推进": {
    text: "四步握手全部完成，主控可以推进到流程中的下一个工步。"
  },
  "握手故障": {
    text: "握手在某一步超时、回读不一致或 PLC 报 Step_Error 而中断，批次进入故障态。",
    detail: "需排除设备故障后处理批次，不得跳过握手直接写参。"
  },
  "PLC_Ready": {
    text: "PLC 侧「允许接收参数」的位信号，四步握手的第一步依据。"
  },
  "Trigger_Write": {
    text: "主控置位以通知 PLC 取新参数的触发信号，第三步依赖它的应答。"
  },
  "Step_ID": {
    text: "主控下发给 PLC 的当前工步标识，PLC 据此选择执行哪一段工艺程序。"
  },
  "Step_Type": {
    text: "工步类型（写参 / 等待 / 人工确认 / 质检），决定该工步是否允许写 PLC。"
  },
  "Step_Running": {
    text: "PLC 置位表示已开始执行本工步（如升温、保温），主控据此进入监视阶段。"
  },
  "Step_Complete": {
    text: "PLC 置位表示本工步正常完成，主控据此收尾并推进到下一工步。"
  },
  "Step_Error": {
    text: "PLC 置位表示本工步执行出错，主控据此报握手故障并停批。"
  },
  "Host_Hold": {
    text: "主控置位请求保持（暂停），等待 PLC 回 PLC_Held 后停剩余时长，禁止盲写下一步。"
  },
  "PLC_Held": {
    text: "PLC 应答已保持，与 Host_Hold 配对完成保持握手。"
  },
  "Error_Code": {
    text: "PLC 报 Step_Error 时带回的错误码，按设备手册定位具体故障。"
  },
  "Heartbeat": {
    text: "PLC 周期刷新的心跳计数，掉心跳判为通讯故障并触发报警。"
  },
  "Temperature": {
    text: "归档实测采样点（温度），工步完成握手步骤 D 后对照规格判定超差。"
  },
  "HoldTime": {
    text: "归档实测采样点（保温时长），工步完成握手步骤 D 后对照规格判定超差。"
  },
  "Pressure": {
    text: "归档实测采样点（压力），工步完成握手步骤 D 后对照规格判定超差。"
  },
  "禁止盲写": {
    text: "严禁绕过 PLC_Ready / 四步握手直接向 PLC 写参数，避免设备带着半成品参数运行。"
  },
  "设备占用": {
    text: "一台设备同一时刻只允许一个处于执行/排队/保持状态的批次，防止两个批次争抢同一套点表。",
    detail: "占用中的设备仍可生成控制配方快照，但不允许启动执行。"
  },
  "Unit Procedure": {
    text: "ISA-88 层级中的单元规程，是一段独立可并行执行的工艺单元（如淬火、回火）。",
    detail: "不同 Unit Procedure 可绑定到不同设备并行进行四步握手；同一设备仍串行。"
  },
  "Operation": {
    text: "ISA-88 层级中的操作，隶属于某个 Unit Procedure。"
  },
  "ISA-88": {
    text: "批次控制国际标准的过程模型层级：Procedure → Unit Procedure → Operation → Phase。"
  },
  "电子签名": {
    text: "审核/放行时要求再次输入登录密码并留痕签署人与签署含义，满足电子记录可追溯要求。"
  },
  "漂移": {
    text: "当前主配方的设定值与本批次冻结快照不一致时，在对比中标记出来的差异。"
  },
  "待放行": {
    text: "批次已执行完成，等待质量对照电子批记录做放行或拒收。"
  },
  "电子批记录": {
    text: "批次执行全过程的归档记录：快照、工步执行、四步握手时序、实测质检与电子签名链。"
  }
};

/** 取术语解释；未收录时返回 undefined，调用方降级为不带提示的普通文本。 */
export function tipOf(key?: string | null): string | undefined {
  if (!key) return undefined;
  const entry = D[key];
  if (!entry) return undefined;
  return entry.detail ? `${entry.text}\n\n${entry.detail}` : entry.text;
}

export function hasTip(key?: string | null): boolean {
  return !!key && key in D;
}
