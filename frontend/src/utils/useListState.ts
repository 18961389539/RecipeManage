/**
 * 记住列表页的「搜索词 / 筛选 / 排序 / 页码」，回到该页时自动还原。
 *
 * 背景：批次、配方、物料三张列表都是整行可点进详情的。用户把列表筛到某几条
 * （如搜「BALM」剩 9 条），点进一条看细节，再点「返回列表」——回来后搜索框空了、
 * 50 条全回来、页码也回到第一页。想继续看刚才那批就得重新输入一遍。
 * 这是高频动作，每次查看详情都罚一次重来。
 *
 * 为什么用 sessionStorage 而不是别的：
 * - 不能用 URL query 装全部状态：搜索词会跟着 URL 走，但 `router.back()` 由浏览器
 *   决定行为，用户从侧栏点回列表、或直接改地址栏时并不经过 history，一样丢。
 * - 不能只靠 `<keep-alive>`：整页缓存会让列表一直保留旧数据，实时批次表反而看不到新批次。
 * - sessionStorage：跟随标签页生命周期，关掉标签即清空，不会把上次的搜索词阴魂不散
 *   地带到明天；同一标签页内的多次往返则稳定命中。
 *
 * 传进来的 ref 沿用页面既有的状态容器（useServerPaging 的 page / sort 等），
 * 这里只负责「读出来铺回去」和「变化时写回去」，不另立一套状态。
 */
import { onScopeDispose, watch, type Ref } from "vue";

export interface ListStateBindings {
  /** 搜索框内容。 */
  query?: Ref<string>;
  /** 筛选值（单选值或字符串化后的多选）。 */
  filter?: Ref<string>;
  /** 页码。 */
  page?: Ref<number>;
  /** 排序键与方向；两者要一起存，单存一个会还原出非法排序。 */
  sort?: Ref<string>;
  order?: Ref<"asc" | "desc">;
}

const PREFIX = "brmes.list.";

function read(key: string): Record<string, unknown> {
  try {
    const raw = sessionStorage.getItem(PREFIX + key);
    return raw ? (JSON.parse(raw) as Record<string, unknown>) : {};
  } catch {
    // 隐私模式下 sessionStorage 可能直接抛错；拿不到就当没有记忆，不影响列表本身。
    return {};
  }
}

function write(key: string, value: Record<string, unknown>) {
  try {
    sessionStorage.setItem(PREFIX + key, JSON.stringify(value));
  } catch {
    /* 写不进去就退化成「不记忆」，功能仍可用 */
  }
}

/** 把存储里的值铺回各个 ref。只认类型匹配的值，存储被别的东西写坏时不至于把列表带进莫名的筛选。 */
function applySaved(saved: Record<string, unknown>, bind: ListStateBindings) {
  if (bind.query && typeof saved.query === "string") bind.query.value = saved.query;
  if (bind.filter && typeof saved.filter === "string") bind.filter.value = saved.filter;
  if (bind.page && typeof saved.page === "number" && saved.page >= 1) bind.page.value = saved.page;
  if (bind.sort && typeof saved.sort === "string") bind.sort.value = saved.sort;
  if (bind.order && (saved.order === "asc" || saved.order === "desc")) bind.order.value = saved.order;
}

/**
 * 只做「铺回」，不注册写回 watcher。给必须在 useServerPaging 之前还原的状态用。
 *
 * 为什么需要这个拆分：useServerPaging 对 search 上的变化挂了 300ms 防抖 watcher。
 * 若在它之后再设 query.value，watcher 会看到一次"变化"并额外拉一次列表——首屏于是
 * 发出两个参数完全相同的请求（实测 /batches 确实发出 2 次 q=BALM）。提前铺回就没有
 * 这次"变化"，只走 onMounted 的那一次加载。写回仍由随后调用的 useListState 负责。
 */
export function restoreListState(key: string, bind: ListStateBindings): void {
  applySaved(read(key), bind);
}

/**
 * 读回上次的排序与方向，供 useServerPaging 当初始值。
 *
 * 排序不能像搜索词那样"构造后再铺"：el-table 的 default-sort 只在初始化读一次，
 * 之后再改 sort ref 表格箭头不跟着走，会出现"页码回来了、排序退回默认"。
 * 所以必须在 useServerPaging 之前就把值拿到，作为构造参数传进去。
 */
export function savedSortOrder(key: string): { sort?: string; order?: "asc" | "desc" } {
  const saved = read(key);
  return {
    sort: typeof saved.sort === "string" ? saved.sort : undefined,
    order: saved.order === "asc" || saved.order === "desc" ? saved.order : undefined
  };
}

/**
 * @param key    记忆键，用列表页的路由路径（如 `/batches`），一页一个。
 * @param bind  要记忆的状态；只传页面真正有的那几个。
 *
 * 用法：在列表页里**先**把状态铺回去、再触发首次取数。
 * 还原动作必须早于 onMounted 的加载，否则会先用默认条件拉一次再重拉。
 */
export function useListState(key: string, bind: ListStateBindings): void {
  applySaved(read(key), bind);

  const stop = watch(
    () => [bind.query?.value, bind.filter?.value, bind.page?.value, bind.sort?.value, bind.order?.value],
    () => {
      const payload: Record<string, unknown> = {};
      if (bind.query) payload.query = bind.query.value;
      if (bind.filter) payload.filter = bind.filter.value;
      if (bind.page) payload.page = bind.page.value;
      if (bind.sort) payload.sort = bind.sort.value;
      if (bind.order) payload.order = bind.order.value;
      write(key, payload);
    },
    { flush: "post" }
  );

  // 详情页来回切换时页面会被卸载重建，watcher 要跟着走，否则同一 key 会叠多个。
  onScopeDispose(stop);
}
