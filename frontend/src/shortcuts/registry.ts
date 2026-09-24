import { onUnmounted, reactive } from "vue";
import type { ShortcutSpec } from "./types";

export type { ShortcutSpec } from "./types";

const pageSources = reactive(new Map<symbol, () => ShortcutSpec[]>());

/** 页面级绑定：卸载时自动摘掉，避免监控页的 F8 漏到配方设计器。 */
export function usePageShortcuts(source: () => ShortcutSpec[]): void {
  const id = Symbol("shortcuts");
  pageSources.set(id, source);
  onUnmounted(() => {
    pageSources.delete(id);
  });
}

export function collectPageShortcuts(): ShortcutSpec[] {
  const out: ShortcutSpec[] = [];
  for (const source of pageSources.values())
    out.push(...source());
  return out;
}
