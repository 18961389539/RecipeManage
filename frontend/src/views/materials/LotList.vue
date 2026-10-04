<template>
  <div>
    <div class="page-title">
      <h2>{{ $t("物料批次谱系") }}<PageGuideButton guide-key="materialLots" /></h2>
      <div>
        <el-button type="primary" @click="openCreate">{{ $t("登记来料批") }}</el-button>
      </div>
    </div>
    <div class="filter-bar">
      <HelpTip term="聚焦本页搜索" chord="/" plain placement="bottom">
        <el-input v-model="query" class="search-field" clearable data-shortcut-search :placeholder="$t('搜索批号 / 物料')" />
      </HelpTip>
      <span v-if="!loading" class="result-count">{{ countText }}</span>
    </div>
    <el-alert class="gap-after"
      :closable="false"
      type="info"
      show-icon
      :title="$t('来料批可拆分成子批投料；生产批次号写入控制配方快照（不纳入完整性哈希）。实验室样品与 PLC 测点分开归档。')"
     
    />
    <el-alert class="gap-after"
      v-if="error"
      :closable="false"
      type="error"
      :title="$t('物料批次加载失败：{0}', [error])"
      show-icon
     
    />
    <el-table
      ref="tableRef"
      :data="items"
      v-loading="loading"
      class="clickable-rows"
      scrollbar-always-on
      max-height="calc(100vh - 320px)"
      :empty-text="query.trim() ? $t('没有匹配的物料批') : $t('暂无物料批次')"
      :default-sort="defaultSort"
      @sort-change="onSortChange"
      @row-click="(row: MaterialLotDto) => $router.push(`/lots/${row.id}`)"
    >
      <el-table-column prop="lotNumber" :label="$t('批次号')" width="180" fixed sortable="custom" :sort-orders="SERVER_ASC_FIRST" />
      <el-table-column prop="materialCode" :label="$t('物料')" width="110" sortable="custom" :sort-orders="SERVER_ASC_FIRST" />
      <el-table-column prop="materialName" :label="$t('名称')" sortable="custom" :sort-orders="SERVER_ASC_FIRST" />
      <el-table-column prop="source" :label="$t('来源')" width="112" sortable="custom" :sort-orders="SERVER_ASC_FIRST">
        <template #default="{ row }">{{ sourceLabel(row.source) }}</template>
      </el-table-column>
      <el-table-column prop="status" :label="$t('状态')" width="112" sortable="custom" :sort-orders="SERVER_ASC_FIRST">
        <template #default="{ row }">
          <el-tag size="small" :type="lotStatusTagType(row.status)" effect="dark">{{ statusLabel(row.status) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="quantity" :label="$t('数量')" width="120" sortable="custom" :sort-orders="SERVER_DESC_FIRST">
        <template #default="{ row }">{{ row.quantity == null ? "—" : `${row.quantity}${row.uom ?? ""}` }}</template>
      </el-table-column>
      <el-table-column prop="createdAt" :label="$t('登记时间')" width="176" sortable="custom" :sort-orders="SERVER_DESC_FIRST" show-overflow-tooltip>
        <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
      </el-table-column>
    </el-table>
    <el-pagination class="pager" layout="total, prev, pager, next" background small :page-size="take"
      :current-page="page" :total="total" hide-on-single-page @current-change="onPageChange" />
    <el-dialog v-model="visible" :title="$t('登记来料批')" width="480px">
      <el-form ref="formRef" :model="form" :rules="rules" label-width="100px" @submit.prevent="create">
        <el-form-item :label="$t('批次号')" prop="lotNumber"><el-input v-model="form.lotNumber" /></el-form-item>
        <el-form-item :label="$t('物料编码')" prop="materialCode"><el-input v-model="form.materialCode" /></el-form-item>
        <el-form-item :label="$t('物料名称')" prop="materialName"><el-input v-model="form.materialName" /></el-form-item>
        <el-form-item :label="$t('数量')" prop="quantity"><el-input-number v-model="form.quantity" :min="0.001" :step="1" /></el-form-item>
        <el-form-item :label="$t('单位')"><el-input v-model="form.uom" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="visible = false">{{ $t("取消") }}</el-button>
        <el-button type="primary" :loading="creating" @click="create">{{ $t("登记") }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { t } from "../../i18n";
import { computed, nextTick, onMounted, reactive, ref } from "vue";
import { useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import type { FormInstance, FormRules, TableInstance } from "element-plus";
import http from "../../api/http";
import type { MaterialLotDto, MaterialLotPageDto } from "../../api/types";
import { lotSourceLabel as sourceLabel, lotStatusLabel as statusLabel, lotStatusTagType } from "../../utils/labels";
import { useLoad } from "../../utils/useLoad";
import { useKeyboardRows } from "../../utils/useKeyboardRows";
import { SERVER_ASC_FIRST, SERVER_DESC_FIRST } from "../../utils/tableSort";
import { useServerPaging } from "../../utils/useServerPaging";
import { formatDateTime } from "../../utils/format";
import HelpTip from "../../components/HelpTip.vue";

const router = useRouter();
const items = ref<MaterialLotDto[]>([]);
const tableRef = ref<TableInstance>();
const query = ref("");
const visible = ref(false);
const creating = ref(false);
const { loading, error, run } = useLoad();
const form = reactive({ lotNumber: "", materialCode: "AL6061", materialName: "铝合金锭", quantity: 100, uom: "kg" });
const pg = useServerPaging({ reload: load, defaultSort: "createdAt", search: query });
const { take, total, page, defaultSort, onSortChange, onPageChange } = pg;
const countText = computed(() => {
  const n = items.value.length;
  return query.value.trim() ? t("{0} / {1} 条", n, total.value) : t("共 {0} 条", total.value);
});

// 整行可点，但 EP 渲染的 tr 不可聚焦——键盘用户此前打不开任何物料批。
useKeyboardRows(tableRef, () => items.value);

const formRef = ref<FormInstance>();
/** 后端 DUP_LOT 的前置版：批号撞了当场说，不要等一次往返。只查已加载的当页，判重以后端为准。 */
function duplicateLotNumber(_rule: unknown, value: unknown, done: (error?: Error) => void) {
  const no = String(value ?? "").trim();
  done(no && items.value.some((l) => l.lotNumber === no) ? new Error(t("物料批次号已存在，请换一个。")) : undefined);
}
const rules = computed<FormRules<typeof form>>(() => ({
  lotNumber: [
    { required: true, message: t("请填写物料批次号。"), trigger: "blur" },
    { validator: duplicateLotNumber, trigger: "blur" }
  ],
  materialCode: [{ required: true, message: t("请填写物料编码。"), trigger: "blur" }]
}));

async function load() {
  await run(http.get<MaterialLotPageDto>("/lots", { params: pg.params() }), (d) => {
    items.value = d.items;
    total.value = d.total;
  });
}

function openCreate() {
  form.lotNumber = `INGOT-${Date.now().toString(36).toUpperCase()}`;
  visible.value = true;
  void nextTick(() => formRef.value?.clearValidate());
}

async function create() {
  if (!(await formRef.value?.validate().catch(() => false))) return;
  creating.value = true;
  try {
    await http.post("/lots", { ...form });
    visible.value = false;
    ElMessage.success(t("已登记来料批"));
    // 回第一页再看：新登记那条按登记时间排在最前，停在第 N 页会让人以为没登记上。
    pg.onFilterChange();
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    creating.value = false;
  }
}

onMounted(load);
</script>
