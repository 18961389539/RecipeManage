<template>
  <div>
    <div class="page-title">
      <div>
        <h2>{{ $t("主配方") }}</h2>
        <span>{{ $t("维护主配方与单元规程。草稿提交后进入多级审核。") }}</span>
      </div>
      <div>
        <el-button v-if="auth.can('ProcessEngineer', 'Quality', 'Supervisor')" :loading="busy === 'export'" @click="exportPkg">{{ $t("导出 JSON") }}</el-button>
        <el-button v-if="auth.can('ProcessEngineer')" :loading="busy === 'import'" @click="pickImport">{{ $t("导入 JSON") }}</el-button>
        <input ref="importInput" type="file" accept="application/json,.json" hidden @change="importPkg" />
        <el-button v-if="auth.can('ProcessEngineer')" type="primary" @click="openCreate">{{ $t("新建配方") }}</el-button>
      </div>
    </div>
    <div class="filter-bar">
      <HelpTip term="聚焦本页搜索" chord="/" plain placement="bottom">
        <el-input v-model="query" class="search-field" clearable data-shortcut-search :placeholder="$t('搜索编码 / 名称 / 产品')" />
      </HelpTip>
      <span v-if="!loading" class="result-count">{{ countText }}</span>
      <el-radio-group v-model="filter" size="small" class="filter-chips">
        <el-radio-button value="all">{{ $t("全部") }}</el-radio-button>
        <el-radio-button value="Draft">{{ $t("草稿") }}</el-radio-button>
        <el-radio-button value="InReview">{{ $t("审核中") }}</el-radio-button>
        <el-radio-button value="Approved">{{ $t("已生效") }}</el-radio-button>
        <el-radio-button value="Rejected">{{ $t("驳回") }}</el-radio-button>
      </el-radio-group>
    </div>
    <el-alert class="gap-after"
      v-if="error"
      :closable="false"
      type="error"
      :title="$t('配方列表加载失败：{0}', [error])"
      show-icon
     
    />
    <el-table
      ref="tableRef"
      :data="shown"
      v-loading="loading"
      class="clickable-rows"
      scrollbar-always-on
      max-height="calc(100vh - 292px)"
      :empty-text="emptyText"
      @row-click="(row: RecipeListItemDto) => $router.push(`/recipes/${row.id}`)"
    >
      <el-table-column prop="code" :label="$t('编码')" width="140" fixed sortable :sort-method="sorters.code" />
      <el-table-column prop="name" :label="$t('名称')" sortable :sort-method="sorters.name" />
      <el-table-column prop="productName" :label="$t('产品')" sortable :sort-method="sorters.productName" />
      <el-table-column prop="approvedVersion" :label="$t('生效版本')" width="116" sortable :sort-method="sorters.approvedVersion" :sort-orders="DESC_FIRST">
        <template #default="{ row }">
          <!-- v 前缀表明这是版本号而非序数；从未生效过的配方（纯草稿/审核中）按全站口径画裸「—」 -->
          <span v-if="row.approvedVersion">v{{ row.approvedVersion }}</span>
          <span v-else>—</span>
        </template>
      </el-table-column>
      <el-table-column min-width="180">
        <template #header><HelpTip term="Unit Procedure">{{ $t("单元规程") }}</HelpTip></template>
        <template #default="{ row }">{{ (row.unitProcedures ?? []).join("、") || "—" }}</template>
      </el-table-column>
      <el-table-column prop="draftStatus" :label="$t('草稿状态')" width="136" sortable :sort-method="sorters.draftStatus">
        <template #default="{ row }">
          <!-- 没有草稿时是 null：空值裸「—」，不套灰色标签（与总览页设备占用表同一口径） -->
          <el-tag v-if="row.draftStatus" size="small" :type="recipeStatusTagType(row.draftStatus)" effect="dark">{{ statusLabel(row.draftStatus) }}</el-tag>
          <span v-else>—</span>
        </template>
      </el-table-column>
      <el-table-column :label="$t('待审节点')" min-width="160">
        <template #default="{ row }">
          {{ row.pendingTitle ? `${row.pendingTitle}${row.reviewVersion ? ` v${row.reviewVersion}` : ""}` : "—" }}
        </template>
      </el-table-column>
    </el-table>
    <el-dialog v-model="createVisible" :title="$t('新建主配方')" width="480px">
      <el-form ref="formRef" :model="form" :rules="rules" label-width="90px" @submit.prevent="create">
        <el-form-item :label="$t('编码')" prop="code"><el-input v-model="form.code" :placeholder="$t('如 AL-HT-01')" /></el-form-item>
        <el-form-item :label="$t('名称')" prop="name"><el-input v-model="form.name" /></el-form-item>
        <el-form-item :label="$t('产品编码')" prop="productCode"><el-input v-model="form.productCode" /></el-form-item>
        <el-form-item :label="$t('产品名称')" prop="productName"><el-input v-model="form.productName" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createVisible = false">{{ $t("取消") }}</el-button>
        <el-button type="primary" :loading="creating" @click="create">{{ $t("创建") }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { t } from "../../i18n";
import { nextTick, onMounted, reactive, ref, computed } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import type { FormInstance, FormRules, TableInstance } from "element-plus";
import http from "../../api/http";
import type { RecipeListItemDto } from "../../api/types";
import { exportRecipePackage, importRecipePackage } from "../../api/recipesTransfer";
import { recipeStatusLabel as statusLabel, recipeStatusOrder, recipeStatusTagType } from "../../utils/labels";
import { useLoad } from "../../utils/useLoad";
import { useKeyboardRows } from "../../utils/useKeyboardRows";
import { DESC_FIRST, byEnum, byNumber, byText } from "../../utils/tableSort";
import { useAuthStore } from "../../stores/auth";
import HelpTip from "../../components/HelpTip.vue";
import { matchesQuery } from "../../utils/format";

const auth = useAuthStore();
const route = useRoute();
const router = useRouter();

const items = ref<RecipeListItemDto[]>([]);
const tableRef = ref<TableInstance>();
const query = ref("");
const createVisible = ref(false);
const creating = ref(false);
const busy = ref("");
const importInput = ref<HTMLInputElement | null>(null);
const form = reactive({ code: "", name: "", productCode: "", productName: "" });
const { loading, error, run } = useLoad();

/** 排序口径见 utils/tableSort：中文按拼音、版本号按数值、状态按生命周期次序。 */
const sorters = {
  code: byText<RecipeListItemDto>((r) => r.code),
  name: byText<RecipeListItemDto>((r) => r.name),
  productName: byText<RecipeListItemDto>((r) => r.productName),
  approvedVersion: byNumber<RecipeListItemDto>((r) => r.approvedVersion),
  draftStatus: byEnum<RecipeListItemDto>((r) => r.draftStatus, recipeStatusOrder)
};

const filter = computed({
  get: () => {
    const raw = route.query.status;
    const value = Array.isArray(raw) ? raw[0] : raw;
    return value && value !== "all" ? String(value) : "all";
  },
  set: (value: string) => {
    void router.replace({ path: "/recipes", query: value === "all" ? {} : { status: value } });
  }
});

const shown = computed(() => items.value.filter((row) => {
  if (filter.value === "Approved") {
    if (!row.approvedVersion) return false;
  } else if (filter.value !== "all" && row.draftStatus !== filter.value)
    return false;
  return matchesQuery(query.value, row.code, row.name, row.productCode, row.productName, ...(row.unitProcedures ?? []));
}));
const countText = computed(() => {
  const n = shown.value.length;
  const total = items.value.length;
  const filtered = !!query.value.trim() || filter.value !== "all";
  return filtered ? t("{0} / {1} 条", n, total) : t("共 {0} 条", total);
});
const emptyText = computed(() => {
  if (query.value.trim()) return t("没有匹配的主配方");
  return filter.value === "all" ? t("暂无主配方") : t("当前筛选条件下没有配方");
});

// 整行可点，但 EP 渲染的 tr 不可聚焦——键盘用户此前打不开任何配方。
useKeyboardRows(tableRef, () => shown.value);

const formRef = ref<FormInstance>();
/** 后端 DUP_CODE 的前置版：编码撞了当场说，不要等一次往返。 */
function duplicateCode(_rule: unknown, value: unknown, done: (error?: Error) => void) {
  const code = String(value ?? "").trim().toUpperCase();
  done(code && items.value.some((r) => r.code.toUpperCase() === code) ? new Error(t("配方编码已存在，请换一个。")) : undefined);
}
const rules = computed<FormRules<typeof form>>(() => ({
  code: [
    { required: true, message: t("请填写配方编码。"), trigger: "blur" },
    { validator: duplicateCode, trigger: "blur" }
  ],
  name: [{ required: true, message: t("请填写配方名称。"), trigger: "blur" }]
}));

async function load() {
  await run(http.get<RecipeListItemDto[]>("/recipes"), (d) => (items.value = d));
}

function openCreate() {
  createVisible.value = true;
  void nextTick(() => formRef.value?.clearValidate());
}

async function create() {
  if (!(await formRef.value?.validate().catch(() => false))) return;
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
  busy.value = "export";
  try {
    await exportRecipePackage();
  } finally {
    busy.value = "";
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
  busy.value = "import";
  try {
    if (await importRecipePackage(file)) await load();
  } finally {
    busy.value = "";
  }
}

onMounted(load);
</script>
