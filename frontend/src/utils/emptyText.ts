import { computed, type ComputedRef, type Ref } from "vue";

/**
 * 表格空态文案的唯一口径：**加载期间一律不显示空态**。
 *
 * 为什么需要它：Element Plus 的 `:empty-text` 只在 `data.length === 0` 时渲染，**完全不看 loading**。
 * 于是首屏（尤其慢网）会先闪一下「暂无主配方」这类**假空态**再跳成真实数据——实测 500~1400ms
 * 与加载遮罩同时存在（用 CDP 限速到 400KB/s 复现）。这正是本仓库空态口径明令禁止的情形：
 * 「加载中不显示、出错不显示（那时『没有数据』是假信息）」。各页此前把这条规则写在了**引导块**上
 * （`v-if="!loading && ..."`），却漏在了 `:empty-text` 本身。
 *
 * 用法：
 *   const emptyText = useEmptyText(loading, () => 关键字按当前搜索词取键);
 *   &lt;el-table :empty-text="emptyText" ... /&gt;
 *
 * 注意注释里不要写出 `t(字面量)` 的**完整调用形态**——覆盖率棘轮会把它当真键扫走并报缺译
 * （实测在本文档注释里写过示例就被棘轮抓出两条）。
 *
 * `resolve` 传**函数**而非值：空态文案常依赖 `t(...)`／筛选条件，需要惰性求值，
 * 且必须走 computed 才能在 loading 翻转时驱动模板重渲染。
 *
 * 命中加载态时返回空串：EP 在空串下仍渲染一个 60px 高的空块（只占位、不吐字）——
 * 遮罩之下看不出来，遮罩一撤数据也到了，不会出现「空白块闪一下」。
 */
export function useEmptyText(loading: Ref<boolean> | ComputedRef<boolean>, resolve: () => string) {
  return computed(() => (loading.value ? "" : resolve()));
}
