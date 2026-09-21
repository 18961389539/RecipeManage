<template>
  <el-container class="shell">
    <el-aside :width="asideWidth" class="aside" :class="{ collapsed: hideLabels, 'drawer-open': drawerOpen }">
      <div class="brand-row">
        <div v-if="!hideLabels" class="brand">BRMES</div>
        <el-button v-if="!isMobile" class="collapse-btn" link @click="collapsed = !collapsed" :title="collapsed ? '展开菜单' : '收起菜单'">
          {{ collapsed ? "»" : "«" }}
        </el-button>
        <el-button v-else class="collapse-btn" link title="关闭菜单" @click="drawerOpen = false">×</el-button>
      </div>
      <div v-if="!hideLabels" class="sub">工艺配方管理与实时执行</div>
      <el-menu
        :router="true"
        :collapse="hideLabels"
        :collapse-transition="false"
        :default-active="$route.path" background-color="var(--sunken)" text-color="var(--text-body)" active-text-color="var(--accent-bright)">
        <!-- 条目与角色全部派生自路由表（router/index.ts menuItems），这里不再维护第二份名单 -->
        <el-menu-item v-for="item in menu" :key="item.path" :index="item.path">
          <el-icon><component :is="item.icon" /></el-icon><span>{{ item.label }}</span>
        </el-menu-item>
      </el-menu>
    </el-aside>
    <el-container>
      <el-header class="header">
        <span class="head-left">
          <el-button v-if="isMobile" class="menu-btn" link title="打开菜单" @click="drawerOpen = true">≡</el-button>
          <span class="section">{{ title }}</span>
        </span>
        <span class="user">
          <RealtimeStatus />
          <template v-if="!isMobile">
            <el-divider direction="vertical" />
            <strong>{{ auth.user?.displayName }}</strong>
          </template>
          <el-tag size="small" effect="dark">{{ userRoleLabel(auth.user?.role) }}</el-tag>
          <el-button link type="primary" @click="logout">退出</el-button>
        </span>
      </el-header>
      <el-main><router-view /></el-main>
    </el-container>
    <div v-if="isMobile && drawerOpen" class="drawer-mask" @click="drawerOpen = false" />
  </el-container>
</template>

<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessageBox } from "element-plus";
import { menuItems } from "../router";
import { useAuthStore } from "../stores/auth";
import { stopExecutionHub } from "../realtime/executionHub";
import { userRoleLabel } from "../utils/labels";
import { useIsMobile } from "../utils/useMedia";
import RealtimeStatus from "../components/RealtimeStatus.vue";

const auth = useAuthStore();
const route = useRoute();
const router = useRouter();
const collapsed = ref(false);
const isMobile = useIsMobile();
const drawerOpen = ref(false);
const menu = computed(() =>
  menuItems().filter((item) => !item.roles || (auth.user?.role && item.roles.includes(auth.user.role)))
);
// 窄屏抽屉必须展开显示文字，桌面端的「收起成图标」偏好不适用于手机
const hideLabels = computed(() => collapsed.value && !isMobile.value);
const asideWidth = computed(() => (isMobile.value ? "232px" : collapsed.value ? "64px" : "232px"));
// 标题唯一来源是路由 meta（见 router/index.ts），与浏览器标签页标题同源。
// 原先在这里按 route.path 前缀硬推，详情页（配方/批次/谱系）只能落到所属大类的名字上。
const title = computed(() => (route.meta.title as string | undefined) ?? "BRMES");

watch(() => route.fullPath, () => { drawerOpen.value = false; });
watch(isMobile, (mobile) => {
  drawerOpen.value = false;
  if (mobile) collapsed.value = false;
});

async function logout() {
  try {
    await ElMessageBox.confirm("确认退出当前账号？", "退出确认", {
      confirmButtonText: "退出",
      cancelButtonText: "取消",
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
.user { display: flex; align-items: center; gap: var(--space-2); flex: none; }
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
  background-color: var(--raised);
  box-shadow: inset 2px 0 0 var(--accent);
}
.aside .el-menu .el-menu-item:hover:not(.is-active) { background-color: var(--hover); }
.header {
  display: flex; align-items: center; justify-content: space-between;
  background: var(--panel); border-bottom: 1px solid var(--line); color: var(--text);
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
}
</style>
