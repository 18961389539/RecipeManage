/**
 * 实时（SignalR）连接状态。
 *
 * 为什么需要：useExecutionHub 用了 withAutomaticReconnect()，断线后会在后台静默重试，
 * 而各页面又有 4 秒轮询兜底——界面看起来数据仍在刷新，实际上执行事件推送早已中断。
 * 对监控系统来说这是最危险的失效模式之一：用户以为看到的是实时状态。
 * 这里把连接状态提到全局，由顶栏 RealtimeStatus 组件展示。
 */
import { defineStore } from "pinia";
import { computed, ref } from "vue";

export type RealtimeStatus = "connecting" | "online" | "reconnecting" | "offline";

export const useRealtimeStore = defineStore("realtime", () => {
  const status = ref<RealtimeStatus>("connecting");
  const lastEventAt = ref<number | null>(null);
  /** 当前成功建立的连接数：多个页面会各自订阅，只有全部断开才算离线。 */
  let activeConnections = 0;

  const isOnline = computed(() => status.value === "online");

  /** 超过 30 秒没收到任何执行事件——即使连接显示在线，也提示数据可能滞后。 */
  const stale = computed(() => {
    if (status.value !== "online" || !lastEventAt.value) return false;
    return Date.now() - lastEventAt.value > 30_000;
  });

  function attached() {
    activeConnections += 1;
    status.value = "online";
  }

  function detached() {
    activeConnections = Math.max(0, activeConnections - 1);
    if (!activeConnections) status.value = "offline";
  }

  function reconnecting() {
    status.value = "reconnecting";
  }

  function reconnected() {
    // 只有仍有活动连接时才恢复在线，避免已卸载页面的回调把状态改回在线。
    if (activeConnections > 0) status.value = "online";
  }

  function failed() {
    status.value = "offline";
  }

  function markEvent() {
    lastEventAt.value = Date.now();
  }

  return { status, lastEventAt, isOnline, stale, attached, detached, reconnecting, reconnected, failed, markEvent };
});
