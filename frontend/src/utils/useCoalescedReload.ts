import { onScopeDispose } from "vue";

/**
 * 把"实时事件一来就整页重拉"合并成一小段一次，并挡住并发重复。
 *
 * 为什么需要：后端握手事件约 100ms 推一次、采样约 400ms 一次，而 SignalR 事件并不携带
 * 页面要展示的完整数据，只能靠重拉补齐。此前 Dashboard / 批次列表对任意事件无条件 load()，
 * 一个运行批次就能打出十几请求/秒——轮询本身 4 秒一次反而不是大头。
 *
 * 语义：尾沿合并。第一次 trigger 起一个固定窗口（不随后续事件顺延，否则高频事件会把刷新饿死）；
 * 窗口内的事件全部丢弃；请求在途时来事件只记一次，等这趟回来后立即补拉，保证不漏掉最新状态。
 */
export function useCoalescedReload(reload: () => unknown, waitMs = 200) {
  let timer: number | undefined;
  let running: Promise<unknown> | null = null;
  let queued = false;
  let disposed = false;

  function fire() {
    timer = undefined;
    running = Promise.resolve().then(reload);
    void running
      .catch(() => undefined)
      .then(() => {
        running = null;
        if (disposed || !queued) return;
        queued = false;
        fire();
      });
  }

  function trigger() {
    if (disposed) return;
    if (running) {
      queued = true;
      return;
    }
    if (timer === undefined) timer = window.setTimeout(fire, waitMs);
  }

  onScopeDispose(() => {
    disposed = true;
    if (timer !== undefined) window.clearTimeout(timer);
  }, true);

  return trigger;
}
