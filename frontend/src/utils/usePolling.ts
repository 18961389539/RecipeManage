import { onUnmounted } from "vue";

/**
 * 数据兜底轮询。实时推送只覆盖"有事件"的场景：断流、无事件或重连失败时，
 * 页面仍要周期重拉，否则停在最后一帧。
 * 间隔常量唯一导出：RealtimeStatus 的"每 4 秒"提示文案也引用它，改这里才不会撒谎。
 */
export const POLL_INTERVAL_MS = 4000;

export function usePolling(task: () => unknown, intervalMs = POLL_INTERVAL_MS) {
  let timer: number | undefined;

  function start() {
    timer ??= window.setInterval(() => void task(), intervalMs);
  }

  function stop() {
    if (timer !== undefined) window.clearInterval(timer);
    timer = undefined;
  }

  onUnmounted(stop);
  return { start, stop };
}