/**
 * canvas 绘图的取色出口。
 *
 * ECharts / uPlot 收到的颜色字符串会被直接画进位图，不走 CSS 层，
 * 因此 `var(--ok)` 在这两处无效（组件里其余地方一律直接写 var()）。
 * 这里从 :root 把变量解析成实际值，色值仍然只有 styles.css 一处定义。
 *
 * 令牌名收成联合类型：写错一个字母会得到空串，图表线条静默消失、极难排查。
 */
export type PaletteToken =
  | "--bg" | "--panel" | "--line" | "--text" | "--text-body" | "--muted"
  | "--accent" | "--accent-bright" | "--ok" | "--warn" | "--err"
  | "--sunken" | "--raised" | "--hover" | "--tint" | "--idle" | "--cool" | "--violet";

export function palette(token: PaletteToken): string {
  return getComputedStyle(document.documentElement).getPropertyValue(token).trim();
}
