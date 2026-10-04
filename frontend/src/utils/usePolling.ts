import { onUnmounted } from "vue";

/**
 * 数据兜底轮询。实时推送只覆盖"有事件"的场景：断流、无事件或重连失败时，
 * 页面仍要周期重拉，否则停在最后一帧。
 * 间隔常量唯一导出：RealtimeStatus 的"每 4 秒"提示文案也引用它，改这里才不会撒谎。
 */
export const POLL_INTERVAL_MS = 4000;

/**
 * 在跑的轮询任务。同一时刻只有一个实时页挂载，所以这是个最多一两个元素的集合；
 * 顶栏徽标点一下就走这条路径立即重拉，而不是等下一个 4 秒。
 */
const live = new Set<() => unknown>();

export function reloadRealtimeNow(): void {
  for (const task of [...live]) void task();
}

export function usePolling(task: () => unknown, intervalMs = POLL_INTERVAL_MS) {
  let timer: number | undefined;
  let running = false;

  /** 上一拍还没回来就跳过这一拍：慢请求（大批次详情、现场弱网）时不再叠罗汉。 */
  async function tick() {
    if (running) return;
    running = true;
    try {
      await task();
    } finally {
      running = false;
    }
  }

  function start() {
    live.add(tick);
    timer ??= window.setInterval(() => void tick(), intervalMs);
  }

  function stop() {
    live.delete(tick);
    if (timer !== undefined) window.clearInterval(timer);
    timer = undefined;
  }

  onUnmounted(stop);
  return { start, stop };
}