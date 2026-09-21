<template>
  <div>
    <div class="page-title">
      <h2>用户与备份</h2>
      <div>
        <el-button :loading="busy === 'export'" @click="exportRecipes">导出配方 JSON</el-button>
        <el-button @click="importRecipes">导入配方 JSON</el-button>
        <input ref="fileEl" type="file" accept="application/json" style="display:none" @change="onImportFile" />
        <el-button v-if="isSqlite" :loading="busy === 'db'" @click="downloadDb">下载 SQLite</el-button>
        <el-button type="primary" @click="openCreate">新建用户</el-button>
      </div>
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
      :title="`用户列表加载失败：${error}`"
      show-icon
     
    />
    <el-table :data="items" v-loading="loading" empty-text="暂无用户">
      <el-table-column prop="userName" label="登录名" width="140" fixed />
      <el-table-column prop="displayName" label="显示名" />
      <el-table-column prop="role" label="角色" width="160">
        <template #default="{ row }">{{ userRoleLabel(row.role) }}</template>
      </el-table-column>
      <el-table-column label="启用" width="80">
        <template #default="{ row }">{{ row.isActive === false ? "停用" : "是" }}</template>
      </el-table-column>
      <el-table-column label="" width="120">
        <template #default="{ row }">
          <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="visible" :title="form.id ? '编辑用户' : '新建用户'" width="460px">
      <el-form label-width="90px">
        <el-form-item label="登录名"><el-input v-model="form.userName" :disabled="!!form.id" /></el-form-item>
        <el-form-item label="显示名"><el-input v-model="form.displayName" /></el-form-item>
        <el-form-item label="角色">
          <el-select v-model="form.role" style="width:100%">
            <el-option v-for="r in roles" :key="r" :label="userRoleLabel(r)" :value="r" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="!form.id" label="密码"><el-input v-model="form.password" type="password" /></el-form-item>
        <el-form-item v-else label="新密码"><el-input v-model="form.newPassword" type="password" placeholder="留空则不改" /></el-form-item>
        <el-form-item v-if="form.id" label="启用"><el-switch v-model="form.isActive" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="visible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { ElMessage } from "element-plus";
import http from "../../api/http";
import type { HealthDto, UserDto, UserRole } from "../../api/types";
import { userRoleLabel } from "../../utils/labels";
import { useLoad } from "../../utils/useLoad";

const items = ref<UserDto[]>([]);
const saving = ref(false);
const busy = ref("");
const { loading, error, run } = useLoad();
const visible = ref(false);
const fileEl = ref<HTMLInputElement | null>(null);
const dbKind = ref("sqlite");
const isSqlite = computed(() => dbKind.value === "sqlite");
const backupHint = computed(() =>
  isSqlite.value
    ? "管理员可维护账号。配方 JSON 不含密码哈希；SQLite 文件含全部本地库（含哈希），仅作灾备。"
    : "当前为 PostgreSQL / JSONB。嵌入式库优先落在 %LOCALAPPDATA%\\BRMES\\pg-embed（已有 Temp 实例则继续复用，避免丢数）。工艺备份请导出配方 JSON。"
);
const roles: UserRole[] = ["Admin", "ProcessEngineer", "Supervisor", "Quality", "Operator"];
const form = reactive({
  id: "",
  userName: "",
  displayName: "",
  role: "Operator" as UserRole,
  password: "",
  newPassword: "",
  isActive: true
});

async function load() {
  await run(http.get<UserDto[]>("/users"), (d) => (items.value = d));
  try {
    dbKind.value = (await http.get<HealthDto>("/health")).data.database ?? "sqlite";
  } catch {
    dbKind.value = "sqlite";
  }
}

function openCreate() {
  Object.assign(form, { id: "", userName: "", displayName: "", role: "Operator", password: "", newPassword: "", isActive: true });
  visible.value = true;
}

function openEdit(row: UserDto) {
  Object.assign(form, {
    id: row.id,
    userName: row.userName,
    displayName: row.displayName,
    role: row.role,
    password: "",
    newPassword: "",
    isActive: row.isActive !== false
  });
  visible.value = true;
}

async function save() {
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

async function exportRecipes() {
  busy.value = "export";
  try {
    const { data } = await http.get("/recipes/export");
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json;charset=utf-8" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = `brmes-recipes-${new Date().toISOString().slice(0, 10)}.json`;
    a.click();
    URL.revokeObjectURL(a.href);
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    busy.value = "";
  }
}

function importRecipes() {
  fileEl.value?.click();
}

async function onImportFile(ev: Event) {
  const file = (ev.target as HTMLInputElement).files?.[0];
  (ev.target as HTMLInputElement).value = "";
  if (!file) return;
  try {
    const text = await file.text();
    const pkg = JSON.parse(text);
    const { data } = await http.post<{ created: number; skipped: number; messages: string[] }>("/recipes/import", pkg);
    ElMessage.success(`导入完成：新建 ${data.created}，跳过 ${data.skipped}`);
    if (data.messages?.length) ElMessage.info(data.messages.slice(0, 3).join("；"));
  } catch (e) {
    ElMessage.error((e as Error).message);
  }
}

async function downloadDb() {
  busy.value = "db";
  try {
    const { data } = await http.get("/admin/sqlite-backup", { responseType: "blob" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(data);
    a.download = `brmes-${new Date().toISOString().slice(0, 10)}.db`;
    a.click();
    URL.revokeObjectURL(a.href);
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    busy.value = "";
  }
}

onMounted(load);
</script>
