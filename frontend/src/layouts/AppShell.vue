<template>
  <el-container class="shell" @keydown.esc.window="closeDrawer">
    <el-aside
      id="app-sidebar"
      :width="asideWidth"
      class="aside"
      :class="{ collapsed: hideLabels, 'drawer-open': drawerOpen }"
      :inert="isMobile && !drawerOpen"
      :aria-hidden="isMobile && !drawerOpen"
    >
      <div class="brand-row">
        <div v-if="!hideLabels" class="brand">BRMES</div>
        <el-button v-if="!isMobile" class="collapse-btn" link @click="collapsed = !collapsed" :title="collapsed ? $t('展开菜单') : $t('收起菜单')">
          {{ collapsed ? "»" : "«" }}
        </el-button>
        <el-button v-else class="collapse-btn" link :title="$t('关闭菜单')" :aria-label="$t('关闭菜单')" @click="drawerOpen = false">×</el-button>
      </div>
      <div v-if="!hideLabels" class="sub">{{ $t("工艺配方管理与实时执行") }}</div>
      <el-menu
        :router="true"
        :collapse="hideLabels"
        :collapse-transition="false"
        :default-active="activeMenuPath" background-color="var(--sunken)" text-color="var(--text-body)" active-text-color="var(--accent-bright)">
        <!-- 条目与角色全部派生自路由表（router/index.ts menuItems），这里不再维护第二份名单 -->
        <el-menu-item v-for="item in menu" :key="item.path" :index="item.path">
          <el-icon><component :is="item.icon" /></el-icon><span>{{ item.label }}</span>
          <!-- 未确认报警计数：不在监控/报警页时也能看到有报警等着处理。aria-hidden 避免数字
               混进菜单项的可访问名（e2e 与读屏按名匹配条目）。收起态 EP 会隐藏文字，一起藏掉。 -->
          <span
            v-if="item.path === '/alarms' && alarmBadge.open > 0 && !hideLabels"
            class="menu-badge"
            aria-hidden="true"
          >{{ alarmBadge.open > 99 ? "99+" : alarmBadge.open }}</span>
        </el-menu-item>
      </el-menu>
    </el-aside>
    <el-container id="app-content" :inert="isMobile && drawerOpen">
      <el-header class="header">
        <span class="head-left">
          <el-button
            v-if="isMobile"
            class="menu-btn"
            link
            :title="$t('打开菜单')"
            :aria-label="$t('打开菜单')"
            aria-controls="app-sidebar"
            :aria-expanded="drawerOpen"
            @click="drawerOpen = true"
          >≡</el-button>
          <span class="section">{{ title }}</span>
        </span>
        <span class="user">
          <!-- 徽标说的是"这一页的执行事件推送"，只订阅了 hub 的页面才有资格报断开。
               电子批记录/配方设计页此前常年挂红「已断开」，被当成系统故障。 -->
          <RealtimeStatus v-if="isRealtimePage" />
          <span class="user-name">
            <el-divider v-if="isRealtimePage" direction="vertical" />
            <strong>{{ sessionName }}</strong>
          </span>
          <HelpTip :term="userRoleLabel(auth.user?.role)" plain placement="bottom">
            <el-tag size="small" effect="dark">{{ userRoleLabel(auth.user?.role) }}</el-tag>
          </HelpTip>
          <HelpTip term="界面语言" plain placement="bottom" block>
            <!-- 只有两种语言，按钮比下拉省一次点击，也不会让车间操作员在菜单里找开关。 -->
            <el-button link type="primary" @click="toggleLocale">
              {{ locale === "en" ? "中文" : "EN" }}
            </el-button>
          </HelpTip>
          <HelpTip term="快捷键" chord="?" plain placement="bottom-end">
            <el-button link type="primary" @click="shortcutHelpOpen = true">
              {{ $t("快捷键") }} <kbd class="shortcut-key">?</kbd>
            </el-button>
          </HelpTip>
          <el-button link type="primary" @click="logout">{{ $t("退出") }}</el-button>
        </span>
      </el-header>
      <el-main><router-view /></el-main>
    </el-container>
    <div v-if="isMobile && drawerOpen" class="drawer-mask" @click="drawerOpen = false" />
  </el-container>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessageBox } from "element-plus";
import { menuItems } from "../router";
import { currentLocale, setLocale, t } from "../i18n";
import type { AppLocale } from "../i18n";
import { useAuthStore } from "../stores/auth";
import { useAlarmBadgeStore } from "../stores/alarms";
import { stopExecutionHub } from "../realtime/executionHub";
import { userRoleLabel } from "../utils/labels";
import { useIsMobile } from "../utils/useMedia";
import { shortcutHelpOpen } from "../shortcuts/state";
import HelpTip from "../components/HelpTip.vue";
import RealtimeStatus from "../components/RealtimeStatus.vue";

const auth = useAuthStore();
const alarmBadge = useAlarmBadgeStore();
const route = useRoute();
const router = useRouter();
const collapsed = ref(false);
// 语言：初值取自 i18n（已读过 localStorage），切换后由 i18n 落盘。
// 存本地而不存用户档案，是为了不加一次迁移——语言是终端/偏好，不是记录的一部分。
const locale = ref<AppLocale>(currentLocale());

function toggleLocale() {
  locale.value = locale.value === "en" ? "zh-CN" : "en";
  setLocale(locale.value);
}
const isMobile = useIsMobile();
const drawerOpen = ref(false);
const menu = computed(() =>
  menuItems().filter((item) => !item.roles || auth.can(...item.roles))
    // 侧栏与页面标题的中文来自路由表（router/index.ts 是唯一名单），这里只做展示层翻译，
    // 不往路由里再塞一份英文常量——那会变成第二个真源。
    .map(item => ({ ...item, label: t(item.label) }))
);
// 详情页归属其最近的侧栏父路由，保证路由深入后仍能辨认当前模块。
const activeMenuPath = computed(() => {
  const currentPath = route.path;
  return menu.value
    .map((item) => item.path)
    .filter((path) => currentPath === path || currentPath.startsWith(`${path}/`))
    .sort((a, b) => b.length - a.length)[0] ?? currentPath;
});
// 演示账号的显示名就是角色名（系统管理员 / 工艺工程师），顶栏再挂角色标签会读成两遍。
const sessionName = computed(() => {
  const user = auth.user;
  if (!user) return "";
  const role = userRoleLabel(user.role);
  if (user.displayName && user.displayName !== role) return user.displayName;
  return user.userName;
});
// 窄屏抽屉必须展开显示文字，桌面端的「收起成图标」偏好不适用于手机
const hideLabels = computed(() => collapsed.value && !isMobile.value);
const asideWidth = computed(() => (isMobile.value ? "232px" : collapsed.value ? "64px" : "232px"));
// 标题唯一来源是路由 meta（见 router/index.ts），与浏览器标签页标题同源。
// 原先在这里按 route.path 前缀硬推，详情页（配方/批次/谱系）只能落到所属大类的名字上。
const title = computed(() => t((route.meta.title as string | undefined) ?? "BRMES"));
const isRealtimePage = computed(() => route.meta.realtime === true);

watch(() => route.fullPath, () => {
  drawerOpen.value = false;
  // 路由切换顺带刷一次报警计数：在报警页确认完离开时，数字不必等下一个 30 秒周期。
  void alarmBadge.refresh();
});
watch(drawerOpen, async (open) => {
  if (!isMobile.value) return;
  await nextTick();
  if (open) {
    document.querySelector<HTMLElement>("#app-sidebar .el-menu-item:not(.is-disabled)")?.focus();
  } else {
    document.querySelector<HTMLButtonElement>(".menu-btn")?.focus();
  }
});
watch(isMobile, (mobile) => {
  drawerOpen.value = false;
  if (mobile) collapsed.value = false;
});
function closeDrawer() {
  if (isMobile.value) drawerOpen.value = false;
}

/**
 * 未确认报警计数：进页面拉一次，之后每 30 秒一次。
 * 取数口径与失败静默的理由见 stores/alarms.ts（这是提示，不是告警通道）。
 */
const ALARM_REFRESH_MS = 30_000;
let alarmTimer: number | undefined;
onMounted(() => {
  void alarmBadge.refresh();
  alarmTimer = window.setInterval(() => void alarmBadge.refresh(), ALARM_REFRESH_MS);
});
onUnmounted(() => {
  if (alarmTimer) clearInterval(alarmTimer);
});

async function logout() {
  try {
    await ElMessageBox.confirm(t("确认退出当前账号？"), t("退出确认"), {
      confirmButtonText: t("退出"),
      cancelButtonText: t("取消"),
      type: "warning"
    });
  } catch {
    return;
  }
  stopExecutionHub();
  auth.logout();
  void router.push("/login");
}
</script>

<style scoped>
.shell { height: 100%; }
/* 菜单项多于可视高度时侧栏自身可滚，而不是把最后的条目裁掉（overflow-x 仍需隐藏，
   否则收起动画期间会出现横向滚动条） */
.aside { background: var(--sunken); border-right: 1px solid var(--line); color: var(--text-body); transition: width 0.2s ease; overflow-x: hidden; overflow-y: auto; }
.brand-row { display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); padding: var(--space-4) var(--space-3) 0 var(--space-5); }
/* 收起态只剩一个 » 按钮，左对齐会显得悬在半空，改为整行居中 */
.aside.collapsed .brand-row { justify-content: center; padding: var(--space-4) 0 0; }
.brand { font-size: 22px; font-weight: 700; letter-spacing: 1px; }
.collapse-btn { color: var(--muted); font-size: 16px; }
.sub { font-size: 12px; color: var(--muted); padding: 0 var(--space-5) var(--space-4); }
.user { display: flex; align-items: center; gap: var(--space-2); flex: none; flex-wrap: wrap; min-width: 0; }
.head-left { display: flex; align-items: center; gap: var(--space-2); min-width: 0; }
/* 菜单项收成圆角磁贴：默认样式是贴满侧栏宽度的直角条，选中项与侧栏边界糊在一起。
   选择器加 .aside/.el-menu 前缀提高特异性，避免用 !important 去压组件库样式。 */
.aside .el-menu-item {
  height: 42px;
  line-height: 42px;
  margin: 2px var(--space-2);
  border-radius: 7px;
  transition: background-color 0.15s ease, color 0.15s ease;
}
.aside .el-menu .el-menu-item.is-active {
  background-color: var(--tint);
  box-shadow: inset 3px 0 0 var(--accent);
}
.aside .el-menu .el-menu-item:hover:not(.is-active) { background-color: var(--hover); }
/* 侧栏未确认报警计数：数字贴右端；红底上用深色字（--bg）保证 11px 数字的对比度 */
.menu-badge {
  margin-left: auto;
  padding: 0 var(--space-1);
  border-radius: 999px;
  background: var(--err);
  color: var(--bg);
  font-size: 11px;
  font-weight: 600;
  line-height: 16px;
  font-variant-numeric: tabular-nums;
}
.header {
  display: flex; align-items: center; justify-content: space-between; gap: var(--space-2);
  background: var(--panel); border-bottom: 1px solid var(--line); color: var(--text);
  min-width: 0;
}
/* 顶栏标题是「当前处于哪一区」的上下文，不是页面主标题——页内 h2 才是。
   此前两者字号接近，同一屏出现两个层级相近的标题。 */
.header .section {
  font-size: 13px; letter-spacing: 0.4px; color: var(--muted);
  /* 中文在 flex 收缩时会逐字竖排（窄屏下顶栏标题变成一列字），必须 nowrap + 省略号 */
  min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
/* ===== ≤768px：侧栏改为覆盖式抽屉 =====
   固定 232px 的侧栏在 390px 宽的手机上会吃掉大半屏，且把主内容挤到不可读。 */
@media (max-width: 768px) {
  .aside {
    position: fixed; top: 0; bottom: 0; left: 0; z-index: 2400;
    max-width: 82vw; width: 232px;
    transform: translateX(-100%);
    transition: transform 0.22s ease;
    box-shadow: 4px 0 18px rgba(0, 0, 0, 0.5);
  }
  .aside.drawer-open { transform: translateX(0); }
  .drawer-mask { position: fixed; inset: 0; z-index: 2300; background: rgba(4, 8, 16, 0.6); }
  .header { padding: 0 var(--space-3); }
  .menu-btn { font-size: 20px; color: var(--text-body); }
  .user { gap: 6px; }
  .user-name { display: none; }
  /* 顶栏这块标题与页面自己的 <h2> 是同一句话（都取自 meta.title），窄屏留一个就够。
     留着它会被挤到省略号收尾（实测「批次实时监控」在 390px 上差 33px），反而像显示坏了。 */
  .header .section { display: none; }
}
/* 顶栏与抽屉头部的图标按钮（≡ / « » / ×）实测只有 15×22，指尖点不准；补到 24×24 的最小命中区。
   只改命中范围与对齐，不改字形大小。 */
.header .el-button.is-link,
.brand-row .el-button.is-link { min-width: 24px; min-height: 24px; }
@media (max-width: 1100px) {
  .user-name { display: none; }
}
@media (prefers-reduced-motion: reduce) {
  .aside, .aside .el-menu-item { transition: none; }
}
</style>
