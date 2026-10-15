<template>
  <div>
    <div class="page-title">
      <div>
        <h2>{{ $t("主配方") }}<PageGuideButton guide-key="recipes" /></h2>
        <span>{{ $t("维护主配方与单元规程。草稿提交后进入多级审核。") }}</span>
      </div>
      <div>
        <el-button v-if="auth.can('ProcessEngineer', 'Quality', 'Supervisor')" :loading="busy === 'export'" @click="exportPkg">{{ $t("导出 JSON") }}</el-button>
        <el-button v-if="auth.can('ProcessEngineer')" :loading="busy === 'import'" @click="pickImport">{{ $t("导入 JSON") }}</el-button>
        <input ref="importInput" type="file" accept="application/json,.json" hidden @change="importPkg" />
        <el-button v-if="auth.can('ProcessEngineer')" type="primary" @click="openCreate">{{ $t("新建配方") }}</el-button>
        <!-- 创建/导入只给工艺工程师、导出还要加主管与质量（见 docs/auth-matrix.md 的 recipe.author /
             recipe.export）。管理员与操作员进这页一个按钮都没有：列表里 50 条配方看着完全正常、
             点进去也能读，却无人说明"我能做什么"，容易被当成权限丢失或页面没加载完。
             与批次页同一处理——只读角色看到一句说明，讲清"只能看"和"该找谁"。 -->
        <span v-else-if="!auth.can('Quality', 'Supervisor')" class="readonly-hint">{{ $t("本页仅供查看；新建与导入配方需要工艺工程师账号。") }}</span>
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
    <!-- 本页没有轮询，取数一旦失败就会一直停在错误态；没这个按钮用户只能刷新整个浏览器
         （或干脆以为页面坏了）。与审计页同一写法：就地重发当前列表请求。 -->
    <el-alert class="gap-after" v-if="error" :closable="false" type="error" show-icon>
      <template #title>
        <span class="alert-title-row">
          {{ $t("配方列表加载失败：{0}", [error]) }}
          <el-button link type="danger" :loading="loading" @click="load">{{ $t("重新加载") }}</el-button>
        </span>
      </template>
    </el-alert>
    <el-table
      ref="tableRef"
      :data="shown"
      v-loading="loading"
      class="clickable-rows"
      scrollbar-always-on
      max-height="calc(100vh - 292px)"
      :empty-text="emptyText"
      :default-sort="{ prop: 'code', order: 'ascending' }"
      @row-click="(row: RecipeListItemDto) => $router.push(`/recipes/${row.id}`)"
    >
      <!-- 配方编码 15 位定长（14px 字体实测最长 136px），此前 140 减去 .cell 左右 padding 24
           只剩 116px，几乎每条都折两行、行高被撑到 46px。放宽到 176 并开省略号兜底。 -->
      <el-table-column prop="code" :label="$t('编码')" width="176" fixed sortable :sort-method="sorters.code" show-overflow-tooltip />
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
    <!-- 空库时给新装机指路：主配方是整条线的起点——没有配方，批次、工步、设定矩阵都无从谈起。
         比"暂无主配方"四个字有用得多。取数失败 / 正在筛选时不出现（那时"还没有配方"是假信息）。
         注意用两个独立调用而不是在调用里做三元选择：覆盖率棘轮只认紧跟左括号的字面量，
         运行时选键会让两个分支**都进不了统计**（漏译也不报警），实测命中数为零。 -->
    <div v-if="!loading && !error && !items.length && !query.trim() && filter === 'all'" class="muted gap-before">
      <template v-if="auth.can('ProcessEngineer')">
        {{ $t("还没有主配方：点右上角「新建配方」建一份，把工步与参数编好、提交审核，批准后才能在「批次执行」创建生产批次。") }}
      </template>
      <template v-else>
        {{ $t("还没有主配方。配方由工艺工程师创建并提交审核；批准后你可以在「批次执行」里用它创建生产批次。") }}
      </template>
    </div>
    <el-dialog v-model="createVisible" :title="$t('新建主配方')" width="480px" :before-close="guardClose">
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
import { useEmptyText } from "../../utils/emptyText";
import { useDialogGuard } from "../../utils/useDialogGuard";
import { useKeyboardRows } from "../../utils/useKeyboardRows";
import { useListState } from "../../utils/useListState";
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
/** 新建表单 4 个字段，误按 Esc 全丢；与全站表单同一道闸。 */
const { markClean, guardClose } = useDialogGuard(() => form);
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

// 默认按编码升序：后端给的是「更新时间倒序」，但表里没有更新时间列，用户看不出是按什么排的
// （设备/用户页都是编码序，本页与它们对齐）。排序器与表头 default-sort 声明同一列，
// 箭头才如实反映看到的顺序；否则箭头会指向一个没显示的字段。
const shown = computed(() =>
  items.value
    .filter((row) => {
      if (filter.value === "Approved") {
        if (!row.approvedVersion) return false;
      } else if (filter.value !== "all" && row.draftStatus !== filter.value)
        return false;
      return matchesQuery(query.value, row.code, row.name, row.productCode, row.productName, ...(row.unitProcedures ?? []));
    })
    .sort(sorters.code)
);
const countText = computed(() => {
  const n = shown.value.length;
  const total = items.value.length;
  const filtered = !!query.value.trim() || filter.value !== "all";
  return filtered ? t("{0} / {1} 条", n, total) : t("共 {0} 条", total);
});
// 加载期间不吐空态（EP 的 :empty-text 不看 loading，慢网首屏会闪「暂无主配方」假空态）。口径见 utils/emptyText.ts。
const emptyText = useEmptyText(loading, () => {
  if (query.value.trim()) return t("没有匹配的主配方");
  return filter.value === "all" ? t("暂无主配方") : t("当前筛选条件下没有配方");
});

// 整行可点，但 EP 渲染的 tr 不可聚焦——键盘用户此前打不开任何配方。
useKeyboardRows(tableRef, () => shown.value);

// 搜索词在「进配方设计器 → 返回列表」后要还原。本页是客户端过滤，筛选 chips 落在 URL query
// 上（见上面的 filter computed），所以只需记搜索词；列表数据本身每次都重取，不会看到陈旧配方。
useListState("/recipes", { query });

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
  // 表单是模块级 reactive：上次填到一半点了取消，再打开还留着旧值，
  // 很容易把上一条的编码/名称顺手提交出去（编码还有唯一性）。
  Object.assign(form, { code: "", name: "", productCode: "", productName: "" });
  createVisible.value = true;
  markClean();
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
