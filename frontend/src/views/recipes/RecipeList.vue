<template>
  <div>
    <div class="page-title">
      <h2>主配方</h2>
      <div>
        <el-button v-if="auth.can('ProcessEngineer', 'Quality', 'Supervisor')" @click="exportPkg">导出 JSON</el-button>
        <el-button v-if="auth.can('ProcessEngineer')" @click="pickImport">导入 JSON</el-button>
        <input ref="importInput" type="file" accept="application/json,.json" hidden @change="importPkg" />
        <el-button v-if="auth.can('ProcessEngineer')" type="primary" @click="createVisible = true">新建配方</el-button>
      </div>
    </div>
    <el-alert class="gap-after"
      v-if="error"
      :closable="false"
      type="error"
      :title="`配方列表加载失败：${error}`"
      show-icon
     
    />
    <el-table
      :data="items"
      v-loading="loading"
      class="clickable-rows"
      empty-text="暂无主配方"
      @row-click="(row: RecipeListItemDto) => $router.push(`/recipes/${row.id}`)"
    >
      <el-table-column prop="code" label="编码" width="140" fixed />
      <el-table-column prop="name" label="名称" />
      <el-table-column prop="productName" label="产品" />
      <el-table-column prop="approvedVersion" label="生效版本" width="100" />
      <el-table-column label="Unit Procedure" min-width="180">
        <template #default="{ row }">{{ (row.unitProcedures ?? []).join("、") || "—" }}</template>
      </el-table-column>
      <el-table-column label="草稿状态" width="120">
        <template #default="{ row }">
          <el-tag size="small" :type="recipeStatusTagType(row.draftStatus)" effect="dark">{{ statusLabel(row.draftStatus) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="待审节点" min-width="160">
        <template #default="{ row }">
          {{ row.pendingLevel ? `${approvalLevelLabel(row.pendingLevel)}${row.reviewVersion ? ` v${row.reviewVersion}` : ""}` : "—" }}
        </template>
      </el-table-column>
    </el-table>
    <el-dialog v-model="createVisible" title="新建主配方" width="480px">
      <el-form :model="form" label-width="90px">
        <el-form-item label="编码"><el-input v-model="form.code" /></el-form-item>
        <el-form-item label="名称"><el-input v-model="form.name" /></el-form-item>
        <el-form-item label="产品编码"><el-input v-model="form.productCode" /></el-form-item>
        <el-form-item label="产品名称"><el-input v-model="form.productName" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createVisible = false">取消</el-button>
        <el-button type="primary" :loading="creating" @click="create">创建</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from "vue";
import { useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import http from "../../api/http";
import type { RecipeImportResultDto, RecipeListItemDto, RecipePackageDto } from "../../api/types";
import { recipeStatusLabel as statusLabel, recipeStatusTagType, approvalLevelLabel } from "../../utils/labels";
import { useLoad } from "../../utils/useLoad";
import { useAuthStore } from "../../stores/auth";

const auth = useAuthStore();

const items = ref<RecipeListItemDto[]>([]);
const createVisible = ref(false);
const creating = ref(false);
const importInput = ref<HTMLInputElement | null>(null);
const router = useRouter();
const form = reactive({ code: "", name: "", productCode: "", productName: "" });
const { loading, error, run } = useLoad();

async function load() {
  await run(http.get<RecipeListItemDto[]>("/recipes"), (d) => (items.value = d));
}

async function create() {
  creating.value = true;
  try {
    const { data } = await http.post("/recipes", form);
    createVisible.value = false;
    await router.push(`/recipes/${data.id}`);
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    creating.value = false;
  }
}

async function exportPkg() {
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
  }
}

function pickImport() {
  importInput.value?.click();
}

async function importPkg(ev: Event) {
  const input = ev.target as HTMLInputElement;
  const file = input.files?.[0];
  input.value = "";
  if (!file) return;
  try {
    const text = await file.text();
    const packageDto = JSON.parse(text) as RecipePackageDto;
    const { data } = await http.post<RecipeImportResultDto>("/recipes/import", packageDto);
    ElMessage.success(`导入完成：新建 ${data.created}，跳过 ${data.skipped}`);
    await load();
  } catch (e) {
    ElMessage.error((e as Error).message);
  }
}

onMounted(load);
</script>
