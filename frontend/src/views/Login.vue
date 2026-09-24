<template>
  <div class="login">
    <el-card class="card">
      <h2>{{ $t("工艺配方管理与实时执行系统") }}</h2>
      <p>{{ $t("离散制造 · 批次配方管理与执行") }}</p>
      <el-form @submit.prevent="onSubmit" label-position="top">
        <el-form-item :label="$t('用户名')">
          <el-input v-model="userName" autocomplete="username" autofocus />
        </el-form-item>
        <el-form-item :label="$t('密码')">
          <el-input v-model="password" type="password" autocomplete="current-password" show-password />
        </el-form-item>
        <el-button type="primary" native-type="submit" :loading="loading" style="width:100%">{{ $t("登录") }}</el-button>
      </el-form>
      <!-- 只在 dev 有内容：生产构建里 accounts 是空数组（明文口令被摇树掉），不留空占位。 -->
      <div v-if="accounts.length" class="roles">
        <span class="roles-label">{{ $t("演示账号 · 点一下填入用户名与密码") }}</span>
        <el-button v-for="a in accounts" :key="a.user" size="small" text @click="pick(a)">{{ $t(a.label) }}</el-button>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { t } from "../i18n";
import { ref } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import { useAuthStore } from "../stores/auth";

type DemoAccount = { user: string; pass: string; label: string };

/**
 * 演示账号只在 dev 与 e2e 下存在（playwright webServer 跑的就是 npm run dev）。
 * import.meta.env.DEV 是编译期常量：生产构建时整个数组连同明文密码一起被摇树移除，
 * 不会进 bundle；预填默认口令同样只在 dev 生效。
 */
const accounts: DemoAccount[] = import.meta.env.DEV
  ? [
      { user: "admin", pass: "Admin@123", label: "管理员" },
      { user: "engineer", pass: "Engineer@123", label: "工艺工程师" },
      { user: "supervisor", pass: "Supervisor@123", label: "工艺主管" },
      { user: "qa", pass: "Quality@123", label: "质量工程师" },
      { user: "operator", pass: "Operator@123", label: "车间操作员" }
    ]
  : [];

const userName = ref(import.meta.env.DEV ? "admin" : "");
const password = ref(import.meta.env.DEV ? "Admin@123" : "");
const loading = ref(false);
const auth = useAuthStore();
const route = useRoute();
const router = useRouter();

/**
 * 会话过期被踢回登录页时，路由守卫会带上 redirect（见 main.ts / router）。
 * 只接受站内绝对路径：//evil.com 也是浏览器眼里的"绝对 URL"，必须挡掉。
 */
function redirectTarget(): string {
  const raw = route.query.redirect;
  if (typeof raw !== "string") return "/dashboard";
  return raw.startsWith("/") && !raw.startsWith("//") ? raw : "/dashboard";
}

function pick(a: (typeof accounts)[number]) {
  userName.value = a.user;
  password.value = a.pass;
}

async function onSubmit() {
  if (!userName.value.trim() || !password.value) {
    ElMessage.warning(t("请输入用户名和密码"));
    return;
  }
  loading.value = true;
  try {
    await auth.login(userName.value, password.value);
    await router.replace(redirectTarget());
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    loading.value = false;
  }
}
</script>

<style scoped>
/* 窄屏下 420px 固定卡宽会顶出视口（手机上右侧被裁一条），改为随视口收缩并留边距 */
.login { height: 100%; display: grid; place-items: center; padding: var(--space-5) var(--space-4); box-sizing: border-box; background: radial-gradient(circle at top, var(--tint), var(--bg)); }
.card { width: min(420px, 100%); background: var(--panel); border: 1px solid var(--line); }
h2 { margin: 0 0 var(--space-2); }
p { color: var(--muted); margin: 0 0 var(--space-4); }
.roles { margin-top: var(--space-3); display: flex; flex-wrap: wrap; gap: var(--space-1); }
/* 演示账号是 dev 专属入口；不写清"点一下会填入"，五个角色名看着像一组导航链接。 */
.roles-label { flex-basis: 100%; color: var(--muted); font-size: 11px; }
</style>
