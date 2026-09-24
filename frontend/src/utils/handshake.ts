/** 与 Domain HandshakeView 同一套投影：状态机相位 → 四步进度 / 汇总串。 */

export const HANDSHAKE_SEPARATOR = " · ";

export const handshakeFourSteps = [
  { key: "A", title: "A 写参", hint: "写入并回读一致", phases: ["WaitingPlcReady", "WritingParameters"] },
  { key: "B", title: "B 应答", hint: "等待工步启动", phases: ["AwaitingPlcAck"] },
  { key: "C", title: "C 看门狗", hint: "心跳与超时监控", phases: ["StepRunning", "HostWait"] },
  { key: "D", title: "D 归档步进", hint: "读实测、复位、步进", phases: ["Completing", "ReadyToAdvance"] }
] as const;

export type HandshakeStepDef = (typeof handshakeFourSteps)[number];

export function parseHandshakeSummary(raw?: string | null): { equipment: string; phase: string }[] {
  if (!raw?.trim()) return [];
  const parts = raw.includes(HANDSHAKE_SEPARATOR)
    ? raw.split(HANDSHAKE_SEPARATOR).map((p) => p.trim()).filter(Boolean)
    : [raw.trim()];
  const lanes: { equipment: string; phase: string }[] = [];
  for (const part of parts) {
    const index = part.indexOf(":");
    if (index <= 0 || index >= part.length - 1) {
      if (parts.length === 1) return [{ equipment: "", phase: part }];
      continue;
    }
    const equipment = part.slice(0, index);
    if (equipment.includes(" ")) {
      if (parts.length === 1) return [{ equipment: "", phase: part }];
      continue;
    }
    lanes.push({ equipment, phase: part.slice(index + 1) });
  }
  return lanes.length ? lanes : [{ equipment: "", phase: raw.trim() }];
}

export function handshakePhaseToken(raw?: string | null): string {
  const parts = parseHandshakeSummary(raw);
  if (parts.length === 1) return parts[0].phase;
  return raw?.trim() ?? "";
}

export function handshakeFourStepIndex(phase?: string | null): number | null {
  const token = handshakePhaseToken(phase);
  const idx = handshakeFourSteps.findIndex((step) => (step.phases as readonly string[]).includes(token));
  return idx >= 0 ? idx : null;
}

export function handshakeDisplayPhase(
  batchStatus?: string | null,
  lanePhase?: string | null,
  batchPhase?: string | null
): string {
  if (batchStatus === "Held") return "Held";
  if (batchStatus === "Faulted") return "Faulted";
  if (batchStatus === "Completed" || batchStatus === "Released" || batchStatus === "DispositionRejected")
    return "ReadyToAdvance";
  const lane = handshakePhaseToken(lanePhase);
  if (
    lane
    && (handshakeFourStepIndex(lane) != null
      || lane === "Faulted"
      || lane === "Held"
      || lane === "AwaitingConfirm"
      || lane === "HostWait")
  )
    return lane;
  return batchPhase ?? "";
}

export function formatHandshakeSummary(lanes: { equipmentCode: string; phase: string }[]): string {
  const rows = lanes.filter((l) => l.phase);
  if (rows.length <= 1) return rows[0]?.phase ?? "";
  return [...rows]
    .sort((a, b) => a.equipmentCode.localeCompare(b.equipmentCode))
    .map((l) => `${l.equipmentCode}:${l.phase}`)
    .join(HANDSHAKE_SEPARATOR);
}
