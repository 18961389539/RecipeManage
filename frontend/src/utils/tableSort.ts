/**
 * 表格列排序的比较器。
 *
 * 为什么需要：列表此前只能按后端返回的顺序看（批次=新建时间倒序），
 * 想找「最早那条故障批」或「按设备归类」就得逐页扫。这里给 el-table 的
 * `sort-method` 提供统一口径，避免各视图各写一份 `a.x > b.x ? 1 : -1`。
 *
 * 三条口径：
 * - 中文按 zh-Hans-CN 排，而不是 UTF-16 码点（码点序下「批次」会排到「报警」后面）；
 *   `numeric: true` 让 P2 排在 P10 前面。
 * - 枚举按 labels.ts 的业务次序排，不按标签拼音。
 * - 空值排最后。注意 EP 的降序是把升序比较结果整体取反（实测，不是交换实参），
 *   所以**降序时空值会翻到最前**——两参数比较器拿不到方向，做不到两个方向都垫底。
 *   对「确认时间」「数量」这类列，降序把空值顶上来其实正是要看的东西，接受这个行为。
 */

/**
 * 时间与数量列用：首点就是「最新 / 最大在前」，第三次点击回到后端默认顺序。
 * 枚举列不要用它——labels 里的次序数组本身就是"该先看谁"的顺序，走默认升序。
 */
export const DESC_FIRST: ("ascending" | "descending" | null)[] = ["descending", "ascending", null];

function textOf(value: string | number | null | undefined): string {
  return value === null || value === undefined ? "" : String(value);
}

/** 空值垫底（仅对升序成立，降序会被 EP 的整体取反翻到顶上，见文件头）。 */
function nullsLast(cmp: number, aBlank: boolean, bBlank: boolean): number {
  if (aBlank && bBlank) return cmp;
  if (aBlank) return 1;
  if (bBlank) return -1;
  return cmp;
}

export function byText<T>(get: (row: T) => string | number | null | undefined) {
  return (a: T, b: T) =>
    nullsLast(
      textOf(get(a)).localeCompare(textOf(get(b)), "zh-Hans-CN", { numeric: true, sensitivity: "base" }),
      textOf(get(a)) === "",
      textOf(get(b)) === ""
    );
}

export function byNumber<T>(get: (row: T) => number | null | undefined) {
  return (a: T, b: T) => {
    const x = get(a);
    const y = get(b);
    const xa = typeof x === "number" && Number.isFinite(x) ? x : null;
    const ya = typeof y === "number" && Number.isFinite(y) ? y : null;
    if (xa === null || ya === null) return nullsLast(0, xa === null, ya === null);
    return xa - ya;
  };
}

export function byTime<T>(get: (row: T) => string | number | Date | null | undefined) {
  return (a: T, b: T) => {
    const x = Date.parse(String(get(a) ?? ""));
    const y = Date.parse(String(get(b) ?? ""));
    const xa = Number.isNaN(x) ? null : x;
    const ya = Number.isNaN(y) ? null : y;
    if (xa === null || ya === null) return nullsLast(0, xa === null, ya === null);
    return xa - ya;
  };
}

/**
 * 服务端分页的表用：把 EP 的 sort-change 翻成后端的 sort/dir 参数。
 * 两个版本对应原来的两条口径——时间/数量列首点看"最新/最大"，文字与枚举列首点看升序。
 * 一律只给两态、不给第三次点击的「取消排序」：服务端排序没有本地兜底可回退，
 * 箭头消失了、数据却还停在用户点过的那一列上。
 */
export const SERVER_DESC_FIRST: ("descending" | "ascending")[] = ["descending", "ascending"];
export const SERVER_ASC_FIRST: ("ascending" | "descending")[] = ["ascending", "descending"];

export function serverSort(
  order: string | null | undefined,
  prop: string | null | undefined,
  fallbackProp = "at"
) {
  return {
    sort: order ? (prop ?? fallbackProp) : fallbackProp,
    dir: order === "ascending" ? ("asc" as const) : ("desc" as const)
  };
}

/** 枚举列：按 labels.ts 导出的次序分组，未知值落到队尾并按文本兜底。 */
export function byEnum<T>(get: (row: T) => string | null | undefined, order: readonly string[]) {
  const rank = new Map(order.map((value, index) => [value, index] as const));
  const at = (row: T) => {
    const v = get(row);
    return v === null || v === undefined || v === "" ? null : (rank.get(v) ?? order.length);
  };
  return (a: T, b: T) => {
    const x = at(a);
    const y = at(b);
    if (x === null || y === null) return nullsLast(0, x === null, y === null);
    return x - y || byText(get)(a, b);
  };
}
