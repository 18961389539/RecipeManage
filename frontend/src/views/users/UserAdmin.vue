<template>
  <div>
    <div class="page-title">
      <div>
        <h2>{{ $t("用户与备份") }}<PageGuideButton guide-key="users" /></h2>
        <span>{{ $t("账号与角色，以及数据库备份。") }}</span>
      </div>
      <div>
        <!-- 配方包入口在配方列表页：后端 ExportAsync/ImportAsync 都不放行 Admin，在这页点了必然 403。 -->
        <el-button :loading="busy === 'db'" :disabled="!!busy || backupLoading" @click="downloadDb">{{ $t("下载 SQLite") }}</el-button>
        <el-button type="primary" @click="openCreate">{{ $t("新建用户") }}</el-button>
      </div>
    </div>
    <div class="filter-bar">
      <HelpTip term="聚焦本页搜索" chord="/" plain placement="bottom">
        <el-input v-model="query" class="search-field" clearable data-shortcut-search :placeholder="$t('搜索登录名 / 显示名')" />
      </HelpTip>
      <span v-if="!loading" class="result-count">{{ countText }}</span>
    </div>
    <el-alert class="gap-after"
      :closable="false"
      type="info"
      show-icon
      :title="backupHint"
     
    />
    <el-alert class="gap-after"
      v-if="error"
      :closable="false"
      type="error"
      :title="$t('用户列表加载失败：{0}', [error])"
      show-icon
     
    />
    <p v-if="isMobile" class="mobile-table-hint">{{ $t("窄屏下左右滑动表格查看其余列和行操作。") }}</p>
    <el-table
      :data="shown"
      v-loading="loading"
      scrollbar-always-on
      :max-height="isMobile ? undefined : 'calc(100vh - 320px)'"
      :empty-text="query.trim() ? $t('没有匹配的用户') : $t('暂无用户')"
      class="users-table"
    >
      <el-table-column prop="userName" :label="$t('登录名')" width="152" fixed sortable :sort-method="sorters.userName" />
      <el-table-column prop="displayName" :label="$t('显示名')" min-width="120" sortable :sort-method="sorters.displayName" />
      <el-table-column prop="role" :label="$t('角色')" width="172" sortable :sort-method="sorters.role">
        <template #default="{ row }">{{ userRoleLabel(row.role) }}</template>
      </el-table-column>
      <el-table-column prop="isActive" :label="$t('启用')" width="92" sortable :sort-method="sorters.isActive" :sort-orders="DESC_FIRST">
        <template #default="{ row }">
          <el-tag size="small" effect="plain" :type="row.isActive === false ? 'info' : 'success'">
            {{ row.isActive === false ? $t("停用") : $t("是") }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="$t('操作')" width="120">
        <template #default="{ row }">
          <el-button link type="primary" @click="openEdit(row)">{{ $t("编辑") }}</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-card shadow="never" class="gap-before backup-card" v-loading="backupLoading">
      <template #header>
        <div class="backup-head">
          <span>{{ $t("数据库每日备份") }}</span>
          <div>
            <el-button size="small" :loading="busy === 'backup'" :disabled="!!busy || backupLoading" @click="runBackup">{{ $t("立即备份一份") }}</el-button>
            <el-button size="small" :loading="busy === 'maint'" :disabled="!!busy || backupLoading" @click="runMaintenance">{{ $t("立即维护") }}</el-button>
            <el-button size="small" :loading="backupLoading" :disabled="!!busy || backupLoading" @click="loadBackupStatus">{{ $t("刷新") }}</el-button>
          </div>
        </div>
      </template>
      <el-alert v-if="backup?.lastError" class="gap-after" :closable="false" type="error" show-icon
        :title="$t('最近一次备份失败：{0}', [backup.lastError])"
      />
      <el-alert v-else-if="backup && backup.enabled && !backup.files.length" class="gap-after" :closable="false" type="warning" show-icon
        :title="$t('还没有落下任何一份备份。定时任务每天 {0} (UTC) 运行，也可以点上面的立即备份。', [backup.atUtc])"
      />
      <el-descriptions :column="isMobile ? 1 : 2" size="small" border>
        <el-descriptions-item :label="$t('自动备份')">
          <el-tag size="small" :type="backup?.enabled ? 'success' : 'info'" effect="plain">
            {{ backup?.enabled ? $t("已启用") : $t("已停用") }}
          </el-tag>
        </el-descriptions-item>
        <el-descriptions-item :label="$t('每天 (UTC)')">{{ backup?.atUtc ?? "—" }}</el-descriptions-item>
        <el-descriptions-item :label="$t('保留份数')">{{ backup?.keep ?? "—" }}</el-descriptions-item>
        <el-descriptions-item :label="$t('下次执行')">{{ backup ? formatDateTime(backup.nextRunAtUtc) : "—" }}</el-descriptions-item>
        <el-descriptions-item :label="$t('备份目录')" :span="2"><code>{{ backup?.directory ?? "—" }}</code></el-descriptions-item>
      </el-descriptions>
      <p v-if="isMobile" class="mobile-table-hint">{{ $t("窄屏下左右滑动表格查看其余列和行操作。") }}</p>
      <el-table :data="backup?.files ?? []" size="small" class="gap-after backup-files-table" :empty-text="$t('暂无备份文件')">
        <el-table-column prop="name" :label="$t('文件')" min-width="240" show-overflow-tooltip />
        <el-table-column :label="$t('大小')" width="110">
          <template #default="{ row }">{{ formatBytes(row.bytes) }}</template>
        </el-table-column>
        <el-table-column :label="$t('时间')" width="180">
          <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
        </el-table-column>
      </el-table>
      <p class="backup-note">{{ $t("备份只留在服务器本机，超出保留份数的旧快照会被删掉；每次成功与失败都写进操作审计。") }}</p>
    </el-card>

    <el-dialog v-model="visible" :title="form.id ? $t('编辑用户') : $t('新建用户')" width="460px">
      <el-form ref="formRef" :model="form" :rules="rules" label-width="90px" @submit.prevent="save">
        <el-form-item :label="$t('登录名')" prop="userName"><el-input v-model="form.userName" :disabled="!!form.id" /></el-form-item>
        <el-form-item :label="$t('显示名')" prop="displayName"><el-input v-model="form.displayName" /></el-form-item>
        <el-form-item :label="$t('角色')">
          <el-select v-model="form.role" style="width:100%">
            <el-option v-for="r in roles" :key="r" :label="userRoleLabel(r)" :value="r" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="!form.id" :label="$t('密码')" prop="password"><el-input v-model="form.password" type="password" show-password /></el-form-item>
        <el-form-item v-if="!form.id" :label="$t('确认密码')" prop="confirmPassword"><el-input v-model="form.confirmPassword" type="password" show-password /></el-form-item>
        <el-form-item v-else :label="$t('新密码')" prop="newPassword"><el-input v-model="form.newPassword" type="password" show-password :placeholder="$t('留空则不改')" /></el-form-item>
        <el-form-item v-if="form.id" :label="$t('确认新密码')" prop="confirmPassword"><el-input v-model="form.confirmPassword" type="password" show-password :placeholder="$t('留空则不改')" /></el-form-item>
        <el-form-item v-if="form.id" :label="$t('启用')">
          <!-- 停用是"影响别人能否干活"的开关：把后果说在开关旁边，不留到用户登录失败才发现。 -->
          <el-switch v-model="form.isActive" />
          <span class="form-hint">{{ $t("停用后该账号无法登录，可随时重新启用。") }}</span>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="visible = false">{{ $t("取消") }}</el-button>
        <el-button type="primary" :loading="saving" @click="save">{{ $t("保存") }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { t } from "../../i18n";
import { computed, nextTick, onMounted, reactive, ref } from "vue";
import { ElMessage } from "element-plus";
import type { FormInstance, FormRules } from "element-plus";
import http from "../../api/http";
import type { BackupFileDto, BackupStatusDto, MaintenanceResultDto, UserDto, UserRole } from "../../api/types";
import { userRoleLabel, userRoleOrder, esignMeaning } from "../../utils/labels";
import { esignPassword } from "../../utils/esign";
import { formatDateTime, formatFileDate, matchesQuery } from "../../utils/format";
import { useLoad } from "../../utils/useLoad";
import { useIsMobile } from "../../utils/useMedia";
import { DESC_FIRST, byEnum, byNumber, byText } from "../../utils/tableSort";
import HelpTip from "../../components/HelpTip.vue";

const items = ref<UserDto[]>([]);
const query = ref("");
const saving = ref(false);
const busy = ref("");
const isMobile = useIsMobile();
const { loading, error, run } = useLoad();
const visible = ref(false);
const backup = ref<BackupStatusDto | null>(null);
const backupLoading = ref(false);

/** 排序口径见 utils/tableSort；角色按新建用户下拉里的次序分组。 */
const sorters = {
  userName: byText<UserDto>((r) => r.userName),
  displayName: byText<UserDto>((r) => r.displayName),
  role: byEnum<UserDto>((r) => r.role, userRoleOrder),
  isActive: byNumber<UserDto>((r) => (r.isActive === false ? 0 : 1))
};
const shown = computed(() =>
  items.value.filter((row) =>
    matchesQuery(query.value, row.userName, row.displayName, userRoleLabel(row.role))
  )
);
const countText = computed(() => {
  const n = shown.value.length;
  const total = items.value.length;
  return query.value.trim() ? t("{0} / {1} 条", n, total) : t("共 {0} 条", total);
});
const backupHint = computed(() =>
  t("管理员可维护账号。库每天自动落一份快照（见下方），「下载 SQLite」是把整库取走一份、需要电子签名；配方包在配方列表页导出导入。"));
const roles: UserRole[] = ["Admin", "ProcessEngineer", "Supervisor", "Quality", "Operator"];
const form = reactive({
  id: "",
  userName: "",
  displayName: "",
  role: "Operator" as UserRole,
  password: "",
  newPassword: "",
  confirmPassword: "",
  isActive: true
});

const formRef = ref<FormInstance>();
/** 长度与唯一性照抄后端 AuthService 的口径（登录名≥3、密码≥8、重名报错），改成填的时候就地说。 */
function duplicateUserName(_rule: unknown, value: unknown, done: (error?: Error) => void) {
  const name = String(value ?? "").trim().toLowerCase();
  const taken = items.value.some((u) => u.id !== form.id && u.userName.toLowerCase() === name);
  done(name && taken ? new Error(t("登录名已存在。")) : undefined);
}
/** 两次输入必须一致（新建比 password、改密比 newPassword）；没打算填/改密码就不校验。 */
function confirmPasswordMatch(_rule: unknown, value: unknown, done: (error?: Error) => void) {
  const target = form.id ? form.newPassword : form.password;
  if (!target) return done();
  done(value === target ? undefined : new Error(t("两次输入的密码不一致。")));
}
const rules = computed<FormRules<typeof form>>(() => ({
  userName: [
    { required: true, message: t("请填写登录名。"), trigger: "blur" },
    { min: 3, message: t("登录名至少 3 位。"), trigger: "blur" },
    { validator: duplicateUserName, trigger: "blur" }
  ],
  displayName: [{ required: true, message: t("请填写显示名。"), trigger: "blur" }],
  password: [
    { required: true, message: t("新建用户必须设置密码。"), trigger: "blur" },
    { min: 8, message: t("密码至少 8 位。"), trigger: "blur" }
  ],
  // 编辑态留空表示不改密，只有填了才校验长度（后端同口径）。
  newPassword: form.newPassword ? [{ min: 8, message: t("密码至少 8 位。"), trigger: "blur" }] : [],
  // 口令打错一个字符就要现场等"登录失败"，这里让两次输入先在本地对上。
  confirmPassword: [{ validator: confirmPasswordMatch, trigger: "blur" }]
}));

async function load() {
  await run(http.get<UserDto[]>("/users"), (d) => (items.value = d));
}

/**
 * 备份状态。整个库就一个 .db 文件，审计履历、控制配方快照、电子签名全在里面，
 * 所以这一页要能当场看到"最近一次是哪天、留了几份、下一次什么时候跑"，而不是等要恢复时才发现没备份。
 */
async function loadBackupStatus() {
  backupLoading.value = true;
  try {
    backup.value = (await http.get<BackupStatusDto>("/system/backups")).data;
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    backupLoading.value = false;
  }
}

async function runBackup() {
  if (busy.value || backupLoading.value) return;
  busy.value = "backup";
  try {
    const { data } = await http.post<BackupFileDto>("/system/backups");
    ElMessage.success(t("已落一份备份：{0}", data.name));
    await loadBackupStatus();
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    busy.value = "";
  }
}

/**
 * 手工跑一轮维护。"没做 VACUUM"是正常结果（批次在跑、或空闲页不值得重写整个文件），
 * 所以这里按 vacuumed 分支说话，不能一律报成功，否则用户以为按钮没反应。
 */
async function runMaintenance() {
  if (busy.value || backupLoading.value) return;
  busy.value = "maint";
  try {
    const { data } = await http.post<MaintenanceResultDto>("/system/maintenance");
    ElMessage.success(data.vacuumed
      ? t("已更新统计信息，VACUUM 回收 {0}。", formatBytes(data.reclaimedBytes))
      : t("已更新统计信息；本轮未做 VACUUM（{0}）。", data.skippedReason ?? t("未启用")));
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    busy.value = "";
  }
}

/** 备份清单里看的是量级，一位小数足够；小于 10 才给小数，免得显示成 "1.00 MB" 这种假精度。 */
function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  const units = ["B", "kB", "MB", "GB", "TB"];
  const at = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / 1024 ** at;
  return `${value >= 10 || at === 0 ? Math.round(value) : value.toFixed(1)} ${units[at]}`;
}

function openCreate() {
  Object.assign(form, { id: "", userName: "", displayName: "", role: "Operator", password: "", newPassword: "", confirmPassword: "", isActive: true });
  visible.value = true;
  // 重开窗口要清掉上一次的红字，否则残留的报错会挂在已经改对的字段上。
  void nextTick(() => formRef.value?.clearValidate());
}

function openEdit(row: UserDto) {
  Object.assign(form, {
    id: row.id,
    userName: row.userName,
    displayName: row.displayName,
    role: row.role,
    password: "",
    newPassword: "",
    confirmPassword: "",
    isActive: row.isActive !== false
  });
  visible.value = true;
  void nextTick(() => formRef.value?.clearValidate());
}

async function save() {
  // 原先只有点「保存」后弹 toast 才说缺什么，长度和重名要等一次后端往返。
  if (!(await formRef.value?.validate().catch(() => false))) return;
  saving.value = true;
  try {
    if (form.id) {
      await http.put(`/users/${form.id}`, {
        displayName: form.displayName,
        role: form.role,
        isActive: form.isActive,
        newPassword: form.newPassword || null
      });
    } else {
      await http.post("/users", {
        userName: form.userName,
        displayName: form.displayName,
        password: form.password,
        role: form.role
      });
    }
    visible.value = false;
    await load();
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    saving.value = false;
  }
}

async function downloadDb() {
  if (busy.value || backupLoading.value) return;
  busy.value = "db";
  try {
    // 整库备份含全部账号口令哈希，后端现在要求 Admin + 电子签名，且改成 POST（密码不进 URL、不被缓存）。
    const password = await esignPassword(
      t("下载 SQLite 整库备份"),
      esignMeaning("admin.sqlite-backup")
    );
    const { data } = await http.post("/admin/sqlite-backup", { password }, { responseType: "blob" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(data);
    a.download = `brmes-${formatFileDate()}.db`;
    a.click();
    URL.revokeObjectURL(a.href);
  } catch (e) {
    ElMessage.error(await readError(e));
  } finally {
    busy.value = "";
  }
}

/** responseType=blob 时错误响应体也是 Blob，直接取 message 会得到一段二进制文本。 */
async function readError(e: unknown): Promise<string> {
  const err = e as { response?: { data?: unknown }; message?: string };
  const data = err.response?.data;
  if (data instanceof Blob) {
    try {
      return (JSON.parse(await data.text()) as { message?: string }).message ?? t("备份失败。");
    } catch {
      return t("备份失败。");
    }
  }
  return (e as Error).message ?? t("备份失败。");
}

onMounted(() => {
  void load();
  void loadBackupStatus();
});
</script>

<style scoped>
.backup-head { display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); flex-wrap: wrap; }
.backup-head > div { display: flex; flex-wrap: wrap; gap: var(--space-1); }
.backup-head > div :deep(.el-button + .el-button) { margin-left: 0; }
.backup-note { color: var(--muted); font-size: 12px; line-height: 1.5; }
.backup-card :deep(.el-descriptions) { margin-bottom: var(--space-3); }
.backup-card :deep(.el-descriptions__content), .backup-card :deep(code) { overflow-wrap: anywhere; }
.users-table :deep(.el-tag) { min-width: 42px; justify-content: center; }
/* 开关旁的后果说明：次要色小字，与表单标签同一行不抢焦点 */
.form-hint { margin-left: var(--space-2); color: var(--muted); font-size: 12px; }
@media (max-width: 768px) {
  .backup-head { align-items: flex-start; }
  .backup-head > div { width: 100%; }
  .backup-head > div :deep(.el-button) { flex: 1 1 auto; min-height: 36px; }
}
</style>
