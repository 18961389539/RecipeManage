/**
 * 让 el-table 的行可以用键盘走到、按 Enter 打开。
 *
 * 背景：列表页的行是整行可点的（`class="clickable-rows"` + `@row-click`），
 * 但 EP 渲染的 `<tr>` 默认不可聚焦——全站只有鼠标一条路，键盘用户打不开任何详情。
 *
 * 为什么在表格根元素上代理按键、而不是注册成全局快捷键：全局拦 Enter 会先于
 * 按钮和表单收到事件，在「焦点停在刷新按钮上」这类场景里把回车吞掉。代理只处理
 * 落在一行上的 Enter，作用域小，也不会与 ShortcutHost 的判定重叠。
 * 激活动作最终派发回行内第一个单元格，走 EP 自己的 row-click，页面不必再维护
 * 一份「行 → 路由」映射；排序和筛选后 DOM 顺序与数组顺序不一致也不会点错行。
 */
import { nextTick, onMounted, onUnmounted, watch, type Ref } from "vue";
import type { TableInstance } from "element-plus";

const ROW_SELECTOR = ".el-table__body tr";
/** 整行可点、但这一行不该被点（审计日志里指向已删除实体的记录），见 styles.css 的 .is-dead。 */
const DEAD_CLASS = "is-dead";

export function useKeyboardRows(
  tableRef: Ref<TableInstance | undefined>,
  source: () => readonly unknown[]
): void {
  let root: HTMLElement | null = null;
  let attached = false;

  function onKeydown(e: KeyboardEvent) {
    if (e.key !== "Enter" || e.isComposing || e.repeat) return;
    const row = (e.target as Element | null)?.closest?.(ROW_SELECTOR) as HTMLElement | null;
    if (!row || row.tabIndex < 0) return;
    e.preventDefault();
    row.querySelector("td")?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
  }

  function apply() {
    root = tableRef.value?.$el ?? null;
    if (!root) return;
    if (!attached) {
      root.addEventListener("keydown", onKeydown);
      attached = true;
    }
    for (const row of root.querySelectorAll<HTMLTableRowElement>(ROW_SELECTOR)) {
      const openable = !row.classList.contains(DEAD_CLASS);
      if (openable && row.tabIndex < 0) {
        row.tabIndex = 0;
        row.setAttribute("aria-keyshortcuts", "Enter");
      } else if (!openable && row.tabIndex >= 0) {
        row.removeAttribute("tabindex");
        row.removeAttribute("aria-keyshortcuts");
      }
    }
  }

  onMounted(() => void nextTick(apply));
  // 轮询每 4 秒换一次数组，数据变化是行增删的唯一来源，比观察 DOM 便宜也不会漏。
  watch(source, () => void nextTick(apply));
  onUnmounted(() => root?.removeEventListener("keydown", onKeydown));
}
