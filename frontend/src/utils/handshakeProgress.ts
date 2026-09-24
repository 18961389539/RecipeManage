import { handshakePhaseLabel } from "./labels";
import { t } from "../i18n";
import {
  handshakeDisplayPhase,
  handshakeFourStepIndex, handshakeFourSteps, handshakePhaseToken, type HandshakeStepDef
} from "./handshake";

/**
 * 四步握手进度条的纯投影：批次状态 + 车道相位 → 每一步的态与状态行文案。
 *
 * 单独成文件（而不是留在 BatchMonitor 的 computed 里）有两个理由：
 * 一是这块是"操作员能不能写下一步"的唯一视觉依据，值得被逐例钉住；
 * 二是它不能放进 `utils/handshake.ts`——那边被 `labels.ts` 引用，反向引 labels 会成环。
 */

export type HandshakeStepState = "done" | "active" | "todo";

export interface HandshakeProgress {
  steps: (HandshakeStepDef & { state: HandshakeStepState })[];
  activeIndex: number;
  faulted: boolean;
}

const FINISHED = ["Completed", "Released", "DispositionRejected"];

/** 相位不在 A–D 映射内（含 Held / AwaitingConfirm）时全部未开始，由状态行说明原因。 */
export function handshakeProgress(
  status?: string | null,
  lanePhase?: string | null,
  batchPhase?: string | null
): HandshakeProgress {
  const current = handshakeDisplayPhase(status, lanePhase, batchPhase);
  const faulted = handshakePhaseToken(current) === "Faulted";
  const idx = status && FINISHED.includes(status)
    ? handshakeFourSteps.length
    : (handshakeFourStepIndex(current) ?? -1);

  const steps = handshakeFourSteps.map((def, i) => {
    let state: HandshakeStepState = "todo";
    if (faulted) state = "todo";
    else if (idx >= handshakeFourSteps.length) state = "done";
    else if (idx >= 0) state = i < idx ? "done" : i === idx ? "active" : "todo";
    return { ...def, state };
  });

  return { steps, activeIndex: idx, faulted };
}

export function handshakeStatusText(
  status?: string | null,
  lanePhase?: string | null,
  batchPhase?: string | null,
  remainingSeconds?: number | null,
  faultMessage?: string | null
): string {
  const current = handshakeDisplayPhase(status, lanePhase, batchPhase);
  const { activeIndex, faulted } = handshakeProgress(status, lanePhase, batchPhase);
  if (faulted) return faultMessage ? t("握手故障 · {0}", faultMessage) : t("握手故障");
  // 先看相位再取标签：handshakePhaseLabel 对空值返回「—」，只判 !label 的话"尚未开始"这条永远走不到。
  if (!handshakePhaseToken(current)) return t("尚未开始四步握手");
  const label = handshakePhaseLabel(current);
  const left = remainingSeconds != null ? ` · ${t("剩余 {0}s", remainingSeconds.toFixed(0))}` : "";
  const which = handshakeFourSteps[activeIndex];
  return `${which ? `${t(which.title)} · ` : ""}${label}${left}`;
}

/** 多车道并行时，页面副标题要逐台报相位，单台才用一个中文标签。 */
export function handshakeSummary(
  status: string | undefined,
  lanes: { equipmentCode: string; phase: string }[],
  currentPhase: string
): string {
  if (lanes.length > 1 && status === "Running")
    return lanes.map((l) => `${l.equipmentCode}:${handshakePhaseLabel(l.phase)}`).join(" · ");
  return handshakePhaseLabel(currentPhase);
}
