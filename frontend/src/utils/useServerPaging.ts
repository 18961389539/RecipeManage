import { onScopeDispose, ref, watch, type Ref } from "vue";
import { serverSort } from "./tableSort";

/**
 * 分页表格的共用状态：页码、每页条数、排序键、总行数，以及配套的 el-table / el-pagination 事件。
 *
 * 为什么要抽出来：这四张表（审计、批次、物料批、报警）都是"翻页取数"，排序必须在服务端做——
 * 客户端只能排当页那 50 条，会给出一个看着合理其实错误的顺序。四份一样的 skip/sort/dir 各写一边，
 * 改一处漏三处的风险比分层更高。
 *
 * `search` 传进来就变成服务端搜索（输入停止 300ms 后重拉，并回到第一页）。
 * 不传就是各视图自己的客户端过滤，不动这里的逻辑。
 */
export function useServerPaging(options: {
  reload: () => unknown;
  defaultSort: string;
  defaultDir?: "asc" | "desc";
  take?: number;
  search?: Ref<string>;
}) {
  const take = options.take ?? 50;
  const dir = options.defaultDir ?? "desc";
  const total = ref(0);
  const page = ref(1);
  const sort = ref(options.defaultSort);
  const order = ref<"asc" | "desc">(dir);
  const defaultSort = {
    prop: options.defaultSort,
    order: dir === "asc" ? ("ascending" as const) : ("descending" as const)
  };

  function onPageChange(next: number) {
    page.value = next;
    void options.reload();
  }

  /** 换筛选或换排序键都要回第一页：停在旧页码上会显示空白表格，像是筛选没命中。 */
  function onFilterChange() {
    page.value = 1;
    void options.reload();
  }

  function onSortChange({ prop, order: next }: { prop?: string | null; order?: string | null }) {
    const parsed = serverSort(next ?? null, prop ?? null, options.defaultSort);
    sort.value = parsed.sort;
    order.value = parsed.dir;
    onFilterChange();
  }

  /** 查询串。空值不发给后端——`?q=` 会让 LIKE '%%' 白扫一遍全表。 */
  function params(extra?: Record<string, string | number | undefined>) {
    const base: Record<string, string> = {
      take: String(take),
      skip: String((page.value - 1) * take),
      sort: sort.value,
      dir: order.value
    };
    const q = options.search?.value.trim();
    if (q) base.q = q;
    for (const [key, value] of Object.entries(extra ?? {}))
      if (value !== undefined && value !== "") base[key] = String(value);
    return base;
  }

  if (options.search) {
    const source = options.search;
    let timer: ReturnType<typeof setTimeout> | undefined;
    watch(source, () => {
      clearTimeout(timer);
      timer = setTimeout(onFilterChange, 300);
    });
    // 页面在防抖窗口内被关掉时定时器还在，回调会去打一个没人要的请求（并碰到已卸载的 ref）。
    onScopeDispose(() => clearTimeout(timer));
  }

  return { take, total, page, sort, order, defaultSort, params, onPageChange, onFilterChange, onSortChange };
}

export type ServerPaging = ReturnType<typeof useServerPaging>;
