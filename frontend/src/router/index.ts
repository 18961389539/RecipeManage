import { createRouter, createWebHistory, type RouteRecordRaw } from "vue-router";
import type { Component } from "vue";
import { ElMessage } from "element-plus";
import { Bell, Box, EditPen, List, Monitor, Odometer, SetUp, Stamp, User, View } from "@element-plus/icons-vue";
import { useAuthStore } from "../stores/auth";
import type { UserRole } from "../api/types";
import { userRoleLabel } from "../utils/labels";
import { t } from "../i18n";

/**
 * 侧边栏菜单的唯一取数点：条目 = 带 meta.nav 的路由，角色 = 路由 meta.roles（与路由守卫同源）。
 * 以前 AppShell 里逐项手写 v-if="auth.can(...)"，与 meta.roles 双份维护，改一漏一即
 * "菜单可见但路由拦截"。加页面只需要在下面数组里声明一次。
 */
export interface NavItem {
  path: string;
  label: string;
  icon: Component;
  roles?: UserRole[];
}

const routes: RouteRecordRaw[] = [
  { path: "/login", meta: { title: "登录" }, component: () => import("../views/Login.vue") },
  {
    path: "/",
    component: () => import("../layouts/AppShell.vue"),
    children: [
      { path: "", redirect: "/dashboard" },
      { path: "dashboard", meta: { title: "运行总览", realtime: true, nav: { label: "运行总览", icon: Odometer } }, component: () => import("../views/Dashboard.vue") },
      { path: "recipes", meta: { title: "配方设计态", nav: { label: "主配方设计", icon: EditPen } }, component: () => import("../views/recipes/RecipeList.vue") },
      { path: "recipes/:id", meta: { title: "配方设计" }, component: () => import("../views/recipes/RecipeDesigner.vue") },
      {
        path: "approvals",
        meta: { roles: ["Supervisor", "Quality"] satisfies UserRole[], title: "多级审核", nav: { label: "多级审核", icon: Stamp } },
        component: () => import("../views/recipes/Approvals.vue")
      },
      {
        path: "approval-chains",
        meta: { roles: ["Admin"] satisfies UserRole[], title: "审批链配置", nav: { label: "审批链配置", icon: SetUp } },
        component: () => import("../views/recipes/ApprovalChainAdmin.vue")
      },
      { path: "batches", meta: { title: "批次执行态", realtime: true, nav: { label: "批次执行", icon: List } }, component: () => import("../views/batches/BatchList.vue") },
      {
        path: "batches/:id/record",
        // 归档凭据按角色收敛（batch.record.view：主管 / 质量 / 管理员），与后端策略同源；
        // 操作员的取样入口在监控页（lot.handle）。
        meta: { roles: ["Admin", "Supervisor", "Quality"] satisfies UserRole[], title: "批次追溯记录" },
        component: () => import("../views/batches/BatchRecord.vue")
      },
      { path: "batches/:id", meta: { title: "批次实时监控", realtime: true }, component: () => import("../views/batches/BatchMonitor.vue") },
      { path: "lots", meta: { title: "物料批次", nav: { label: "物料谱系", icon: Box } }, component: () => import("../views/materials/LotList.vue") },
      { path: "lots/:id", meta: { title: "物料谱系" }, component: () => import("../views/materials/LotGenealogy.vue") },
      {
        path: "alarms",
        meta: { roles: ["Admin", "Operator", "Supervisor", "Quality"] satisfies UserRole[], title: "过程报警", realtime: true, nav: { label: "过程报警", icon: Bell } },
        component: () => import("../views/alarms/AlarmList.vue")
      },
      {
        path: "equipment",
        meta: { roles: ["Admin", "Operator", "Supervisor", "ProcessEngineer"] satisfies UserRole[], title: "设备与驱动", realtime: true, nav: { label: "设备 / PLC", icon: Monitor } },
        component: () => import("../views/equipment/EquipmentList.vue")
      },
      {
        path: "audit",
        // 全局审计日志只有质量与管理员可读（audit.view）：与后端策略同源，菜单/守卫共用这张名单。
        meta: { roles: ["Admin", "Quality"] satisfies UserRole[], title: "操作审计", nav: { label: "操作审计", icon: View } },
        component: () => import("../views/audit/AuditLog.vue")
      },
      {
        path: "users",
        meta: { roles: ["Admin"] satisfies UserRole[], title: "用户与备份", nav: { label: "用户与备份", icon: User } },
        component: () => import("../views/users/UserAdmin.vue")
      },
      // 兜底页放在壳内：保留侧边栏，用户不必退回地址栏就能改道。
      { path: ":pathMatch(.*)*", meta: { title: "页面不存在" }, component: () => import("../views/NotFound.vue") }
    ]
  }
];

export function menuItems(): NavItem[] {
  const shell = routes.find((r) => r.path === "/");
  return (shell?.children ?? []).flatMap((child) => {
    const nav = child.meta?.nav as { label: string; icon: Component } | undefined;
    if (!nav) return [];
    return [{
      path: `/${child.path}`,
      label: nav.label,
      icon: nav.icon,
      roles: child.meta?.roles as UserRole[] | undefined
    }];
  });
}

const router = createRouter({
  history: createWebHistory(),
  routes
});

router.beforeEach((to) => {
  const auth = useAuthStore();
  // 带上来源路径：会话过期被踢出时（见 main.ts 的 reportSessionExpired）重新登录可回到原页面，
  // 而不是每次都被丢回总览、丢掉刚才填了一半的表单。
  if (to.path !== "/login" && !auth.isAuthed) return { path: "/login", query: { redirect: to.fullPath } };
  if (to.path === "/login" && auth.isAuthed) return "/dashboard";
  const roles = to.matched.map((r) => r.meta.roles as UserRole[] | undefined).find((r) => r?.length);
  if (roles && !auth.can(...roles)) {
    // 权限不足仍照常跳回总览，但必须说明原因：静默跳走会让人以为点了没反应或系统出错。
    // 页面名要单独过 t()：meta.title 是中文名单，直接插进整句会让英文界面夹一个中文页名。
    const page = to.meta.title ? t(String(to.meta.title)) : t("该页面");
    ElMessage.warning({
      message: t("当前账号（{0}）无权访问「{1}」，已回到运行总览。", userRoleLabel(auth.user?.role), page),
      duration: 5000
    });
    return "/dashboard";
  }
  return true;
});

const APP_NAME = "BRMES 工艺配方管理";
router.afterEach((to) => {
  const title = to.meta.title as string | undefined;
  // 浏览器标签页也是界面：不译的话，英文界面会在标签上留一串中文。
  document.title = title ? `${t(title)} · ${t(APP_NAME)}` : t(APP_NAME);
});

export default router;
