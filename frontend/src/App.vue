<template>
  <!-- Element Plus 自带文本（分页、日期面板、确定/取消）跟着语言走；
       业务文案在各自的 $t 里，两者互不干扰。 -->
  <el-config-provider :locale="epLocale">
    <div v-if="navigating" class="route-progress" />
    <router-view />
    <ShortcutHost />
  </el-config-provider>
</template>

<script setup lang="ts">
import { computed, onUnmounted, ref } from "vue";
import { useRouter } from "vue-router";
import ShortcutHost from "./shortcuts/ShortcutHost.vue";
import { elementLocale } from "./i18n";

// elementLocale() 内部读的是 i18n.global.locale 这个 ref，所以在 computed 里调用即随切换重算。
const epLocale = computed(elementLocale);

// 每个页面都是懒加载 chunk，冷启动首次进入某个页面时要几百毫秒才有内容。
// 这段空窗期界面完全静止，用户以为点击丢了，会重复点或怀疑系统卡死。
const navigating = ref(false);
let showTimer: number | undefined;

const router = useRouter();
function finish() {
  if (showTimer) clearTimeout(showTimer);
  navigating.value = false;
}
// beforeEach -> afterEach 之间恰好覆盖了路由组件的解析与加载。
const stopBefore = router.beforeEach(() => {
  // 延迟显示：命中已加载过的 chunk 时导航几乎瞬时完成，进度条一闪反而更扰。
  showTimer = window.setTimeout(() => (navigating.value = true), 150);
});
const stopAfter = router.afterEach(finish);
// 组件 chunk 加载失败时不会走 afterEach，不收尾就留下一条永远跑不完的进度条。
const stopError = router.onError(finish);

onUnmounted(() => {
  stopBefore();
  stopAfter();
  stopError();
  if (showTimer) clearTimeout(showTimer);
});
</script>
