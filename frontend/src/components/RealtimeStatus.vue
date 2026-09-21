<template>
  <el-tooltip :content="tip" placement="bottom-end" effect="dark" popper-class="help-tip-pop">
    <span class="rt" :class="klass">
      <i class="rt-dot" />{{ text }}
    </span>
  </el-tooltip>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from "vue";
import { useRealtimeStore } from "../stores/realtime";
import { POLL_INTERVAL_MS } from "../utils/usePolling";

// 文案里的轮询口径引用唯一常量，改 usePolling 间隔时这里不会撒谎。
const pollSecs = POLL_INTERVAL_MS / 1000;

const rt = useRealtimeStore();
// store 里的 stale 依赖 Date.now()，本身不会自动触发重算，这里定时强制刷新一次。
const tick = ref(0);
const timer = ref<number | undefined>(undefined);
onMounted(() => {
  timer.value = window.setInterval(() => tick.value++, 15_000);
});
onUnmounted(() => {
  if (timer.value) clearInterval(timer.value);
});

const text = computed(() => {
  void tick.value;
  switch (rt.status) {
    case "online": return "实时";
    case "reconnecting": return "重连中";
    case "offline": return "已断开";
    default: return "连接中";
  }
});

const klass = computed(() => {
  void tick.value;
  if (rt.status === "online") return rt.stale ? "warn" : "ok";
  return rt.status === "connecting" ? "warn" : "bad";
});

// 轮询间隔唯一取数点是 POLL_INTERVAL_MS（pollSecs 在文件头定义）；文案不手写"4 秒"，改常量才不会撒谎。
const tip = computed(() => {
  void tick.value;
  switch (rt.status) {
    case "online":
      return rt.stale
        ? `实时推送连接正常，但已超过 30 秒未收到执行事件（当前可能没有批次在运行）。数据仍会每 ${pollSecs} 秒轮询刷新。`
        : "实时推送已连接：握手阶段变化、报警、设备占用即时到达。";
    case "reconnecting":
      return `实时推送正在重连。页面仍每 ${pollSecs} 秒轮询刷新，但执行事件可能滞后——不要据此判断瞬时状态。`;
    case "offline":
      return `实时推送已断开，目前仅靠 ${pollSecs} 秒轮询维持，数据可能滞后。请检查后端服务与网络。`;
    default:
      return "正在建立实时推送连接…";
  }
});
</script>

<style scoped>
.rt { display: inline-flex; align-items: center; gap: 6px; font-size: 12px; cursor: help; color: var(--muted); }
.rt-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--muted); }
.rt.ok .rt-dot { background: var(--ok); }
.rt.warn { color: var(--warn); }
.rt.warn .rt-dot { background: var(--warn); }
.rt.bad { color: var(--err); }
.rt.bad .rt-dot { background: var(--err); }
</style>
