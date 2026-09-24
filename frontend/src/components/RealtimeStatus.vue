<template>
  <el-tooltip :content="tip" placement="bottom-end" effect="dark" popper-class="help-tip-pop">
    <button type="button" class="rt" :class="klass" @click="refreshNow">
      <i class="rt-dot" />{{ text }}
      <span v-if="syncText" class="rt-sync">{{ syncText }}</span>
    </button>
  </el-tooltip>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from "vue";
import { useRealtimeStore } from "../stores/realtime";
import { POLL_INTERVAL_MS, reloadRealtimeNow } from "../utils/usePolling";
import { lastSyncAt } from "../realtime/syncClock";
import { formatClock } from "../utils/format";
import { t } from "../i18n";

// 文案里的轮询口径引用唯一常量，改 usePolling 间隔时这里不会撒谎。
const pollSecs = POLL_INTERVAL_MS / 1000;

const rt = useRealtimeStore();
// stale / 最后同步时间都依赖 Date.now()，本身不会自动触发重算，这里定时强制刷新一次。
// 步长取轮询间隔：显示出来的"最后同步"最多滞后一个轮询周期，不会看着像卡住的表。
const tick = ref(0);
const timer = ref<number | undefined>(undefined);
// 顶栏只在登录后挂载，所以"挂载至今"就是"这个页面应该开始拿数据了"的起点。
const mountedAt = ref(Date.now());
onMounted(() => {
  mountedAt.value = Date.now();
  timer.value = window.setInterval(() => tick.value++, POLL_INTERVAL_MS);
});
onUnmounted(() => {
  if (timer.value) clearInterval(timer.value);
});

const syncedFor = computed(() => {
  void tick.value;
  return lastSyncAt.value ? Date.now() - lastSyncAt.value : null;
});

/**
 * 连丢三个轮询周期还没拿到成功的读请求 = 屏幕上的数字已经不新鲜了。
 * 「一次都没成功过」也算停滞：冷启动就断供的页面，lastSyncAt 一直是 0，
 * 只看 syncedFor 会把它判成"不陈旧"，于是顶栏在什么都没拿到的时候写着「实时」。
 */
const syncStale = computed(() => {
  void tick.value;
  const limit = POLL_INTERVAL_MS * 3;
  return syncedFor.value !== null ? syncedFor.value > limit : Date.now() - mountedAt.value > limit;
});

// 徽标文字在每一页顶栏都可见，所以这几个短词也要过 t()（脚本里的字面量，视图扫不到）。
const text = computed(() => {
  void tick.value;
  switch (rt.status) {
    case "online": return t(syncStale.value ? "刷新停滞" : "实时");
    case "reconnecting": return t("重连中");
    case "offline": return t("已断开");
    default: return t("连接中");
  }
});

const syncText = computed(() => (lastSyncAt.value ? formatClock(lastSyncAt.value) : ""));

const klass = computed(() => {
  void tick.value;
  if (rt.status !== "online") return rt.status === "connecting" ? "warn" : "bad";
  return rt.stale || syncStale.value ? "warn" : "ok";
});

// 轮询间隔唯一取数点是 POLL_INTERVAL_MS（pollSecs 在文件头定义）；文案不手写"4 秒"，改常量才不会撒谎。
// 每段都是带占位的整句键：英文里量词与括号的位置不同，半句拼出来的句子在另一种语言里必然错。
const tip = computed(() => {
  void tick.value;
  const ago = syncedFor.value !== null ? t("（{0} 秒前）", Math.round(syncedFor.value / 1000)) : "";
  const sync = lastSyncAt.value
    ? t("最后一次成功取数：{0}。", `${syncText.value}${ago}`)
    : t("还没有成功的取数请求。");
  const base = t("{0}点一下立即重拉本页数据，不必等下一个 {1} 秒周期。", sync, pollSecs);
  switch (rt.status) {
    case "online":
      return syncStale.value
        // 「一次都没取到」和「取到过但停了 N 秒」是两句话：后者能给秒数，前者报 0 秒就是假信息。
        ? syncedFor.value === null
          ? t("推送连接正常，但还没有取到过一次数据——接口在报错或后端不可达，屏幕上的数字不可信。{0}", base)
          : t("推送连接正常，但已经 {0} 秒没有取到数据——接口在报错或后端不可达，屏幕上的数字可能不是最新。{1}",
            Math.round(syncedFor.value / 1000), base)
        : t("实时推送已连接：握手阶段变化、报警、设备占用即时到达；数据仍每 {0} 秒轮询兜底。{1}{2}",
          pollSecs, rt.stale ? t("（30 秒内没有执行事件，多半是当下没有批次在跑。）") : "", base);
    case "reconnecting":
      return t("实时推送正在重连。页面仍每 {0} 秒轮询刷新，但执行事件可能滞后——不要据此判断瞬时状态。{1}", pollSecs, base);
    case "offline":
      return t("实时推送已断开，目前仅靠 {0} 秒轮询维持，数据可能滞后。请检查后端服务与网络。{1}", pollSecs, base);
    default:
      return t("正在建立实时推送连接…{0}", base);
  }
});

function refreshNow() {
  reloadRealtimeNow();
}
</script>

<style scoped>
.rt { display: inline-flex; align-items: center; gap: 6px; min-height: 24px; font-family: inherit; font-size: 12px; line-height: 1.4; cursor: pointer; color: var(--muted); background: none; border: 0; padding: 0; }
.rt-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--muted); flex: none; }
.rt.ok .rt-dot { background: var(--ok); }
.rt.warn { color: var(--warn); }
.rt.warn .rt-dot { background: var(--warn); }
.rt.bad { color: var(--err); }
.rt.bad .rt-dot { background: var(--err); }
/* 时间戳是"数据新鲜度"的读数，必须读得清：--idle 在 12px 上只有 4.42:1，差一点点不达 4.5:1。
   层级改由字号承担（11px 的 --muted 仍是 7.08:1）。 */
.rt-sync { color: var(--muted); font-size: 11px; font-variant-numeric: tabular-nums; }
.rt:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }
</style>
