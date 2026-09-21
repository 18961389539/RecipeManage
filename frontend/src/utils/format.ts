/**
 * 展示层格式化。
 *
 * 背景：同一份时间数据在不同表格里长相不一——有的手工 toLocaleString()，
 * 有的直接用 prop 把 ISO 串（2026-09-19T15:30:00Z）甩给操作员。这里统一收口。
 */

const DATE_TIME_OPTIONS: Intl.DateTimeFormatOptions = {
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit"
};

const DATE_OPTIONS: Intl.DateTimeFormatOptions = {
  year: "numeric",
  month: "2-digit",
  day: "2-digit"
};

function toDate(value: string | number | Date): Date | null {
  const d = value instanceof Date ? value : new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
}

/** 时间展示统一为 `2026/09/19 23:30:00`；空值显示 —，非法值原样返回便于排查。 */
export function formatDateTime(value?: string | number | Date | null): string {
  if (value === null || value === undefined || value === "") return "—";
  const d = toDate(value);
  if (!d) return String(value);
  return d.toLocaleString("zh-CN", DATE_TIME_OPTIONS);
}

export function formatDate(value?: string | number | Date | null): string {
  if (value === null || value === undefined || value === "") return "—";
  const d = toDate(value);
  if (!d) return String(value);
  return d.toLocaleDateString("zh-CN", DATE_OPTIONS);
}

/** 数字紧凑展示，用于数量/试验值等大数场景。 */
export function formatNumber(value?: number | null): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "—";
  return value.toLocaleString("zh-CN");
}
