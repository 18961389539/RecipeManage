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

/** 本地时分秒紧凑串，给批次号等现场编号，避免 toISOString 的 UTC 时差。 */
export function formatCompactStamp(d = new Date()): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

/** 导出文件名的本地日期戳。同样不用 toISOString：凌晨导出会被写成前一天的日期。 */
export function formatFileDate(d = new Date()): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

/** 本地"时:分:秒"，给"最后同步"这类需要秒级新鲜度读数的角落使用。 */
export function formatClock(value?: string | number | Date | null): string {
  const d = value === null || value === undefined || value === "" ? null : toDate(value);
  if (!d) return "";
  const p = (n: number) => String(n).padStart(2, "0");
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

/** 列表搜索：任一字段包含关键字即命中；空关键字显示全部。 */
export function matchesQuery(query: string, ...parts: Array<string | number | null | undefined>): boolean {
  const q = query.trim().toLowerCase();
  if (!q) return true;
  return parts.some((p) => p != null && String(p).toLowerCase().includes(q));
}
