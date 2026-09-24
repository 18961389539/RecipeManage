/**
 * 控制配方快照完整性（SHA-256 密封）四态的中文文案。
 * 批次监控与批次记录共用同一映射，避免两份译法分叉。
 */
import { t } from "../i18n";

export function snapshotIntegrityLabel(value?: string | null): string {
  if (!value) return "";
  const map: Record<string, string> = {
    Valid: "完整性有效",
    Legacy: "历史快照(无哈希)",
    Mismatch: "完整性失败",
    Corrupt: "快照损坏"
  };
  return t(map[value] ?? value);
}