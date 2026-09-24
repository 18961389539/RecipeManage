<template>
  <div>
    <div class="page-title">
      <div>
        <h2>{{ $t("操作审计") }}</h2>
        <span>{{ $t("配方、批次、设备与账号的关键操作留痕，可按实体还原一次闭环。") }}</span>
      </div>
      <div>
        <el-button :loading="loading" @click="load">{{ $t("刷新") }}</el-button>
      </div>
    </div>
    <div class="filter-bar">
      <HelpTip term="聚焦本页搜索" chord="/" plain placement="bottom">
        <el-input v-model="query" class="search-field" clearable data-shortcut-search :placeholder="$t('搜索用户 / 动作 / 详情')" />
      </HelpTip>
      <el-select v-model="entityType" clearable :placeholder="$t('全部实体')" style="width:168px" @change="onFilterChange">
        <el-option :label="$t('主配方')" value="MasterRecipe" />
        <el-option :label="$t('生产批次')" value="ProductionBatch" />
        <el-option :label="$t('设备')" value="EquipmentLine" />
        <el-option :label="$t('相模板')" value="PhaseTemplate" />
        <el-option :label="$t('用户')" value="AppUser" />
        <el-option :label="$t('物料批')" value="MaterialLot" />
        <el-option :label="$t('实验室样品')" value="LabSample" />
        <el-option :label="$t('过程报警')" value="ProcessAlarm" />
      </el-select>
      <span v-if="!loading" class="result-count">{{ countText }}</span>
    </div>
    <el-alert class="gap-after"
      v-if="error"
      :closable="false"
      type="error"
      :title="`审计记录加载失败：${error}`"
      show-icon
     
    />
    <el-table ref="tableRef" :data="shown" size="small" v-loading="loading" :empty-text="$t('暂无符合条件的审计记录')" class="clickable-rows" scrollbar-always-on max-height="calc(100vh - 280px)" :row-class-name="rowClass" :default-sort="defaultSort" @sort-change="onSortChange" @row-click="openEntity">
      <el-table-column prop="at" :label="$t('时间')" width="172" fixed sortable="custom" :sort-orders="SERVER_DESC_FIRST">
        <template #default="{ row }">{{ formatDateTime(row.at) }}</template>
      </el-table-column>
      <el-table-column prop="userName" :label="$t('用户')" width="108" show-overflow-tooltip sortable="custom" :sort-orders="SERVER_ASC_FIRST" />
      <!-- 动作/实体两列显示的是中文标签，排的是原始 code，点了会出现"箭头在、顺序看着不对"——不给它们排序。 -->
      <el-table-column :label="$t('动作')" min-width="132" show-overflow-tooltip>
        <template #default="{ row }">{{ auditActionLabel(row.action) }}</template>
      </el-table-column>
      <el-table-column prop="entityType" :label="$t('实体')" width="108">
        <template #default="{ row }">{{ auditEntityLabel(row.entityType) }}</template>
      </el-table-column>
      <el-table-column prop="detail" :label="$t('详情')" min-width="220" show-overflow-tooltip sortable="custom" :sort-orders="SERVER_ASC_FIRST" />
    </el-table>
    <el-pagination class="pager" layout="total, prev, pager, next" background small :page-size="take"
      :current-page="page" :total="total" hide-on-single-page @current-change="onPageChange" />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import type { TableInstance } from "element-plus";
import http from "../../api/http";
import type { AuditLogDto, AuditLogPageDto } from "../../api/types";
import { auditActionLabel, auditEntityLabel } from "../../utils/labels";
import { formatDateTime, matchesQuery } from "../../utils/format";
import { useLoad } from "../../utils/useLoad";
import { t } from "../../i18n";
import { SERVER_ASC_FIRST, SERVER_DESC_FIRST } from "../../utils/tableSort";
import { useServerPaging } from "../../utils/useServerPaging";
import { useKeyboardRows } from "../../utils/useKeyboardRows";
import HelpTip from "../../components/HelpTip.vue";

const router = useRouter();
const items = ref<AuditLogDto[]>([]);
const query = ref("");
const entityType = ref("");
const { loading, error, run } = useLoad();
const shown = computed(() =>
  items.value.filter((row) =>
    matchesQuery(
      query.value,
      row.userName,
      auditActionLabel(row.action),
      row.action,
      auditEntityLabel(row.entityType),
      row.entityType,
      row.detail,
      row.entityId
    )
  )
);
const countText = computed(() => {
  const n = shown.value.length;
  if (query.value.trim()) return t("{0} / {1} 条", n, items.value.length);
  // 带占位而不是拼好的串：英文里量词与数字的位置不同，拼出来的那句没法翻。
  return total.value ? t("本页 {0} 条 · 共 {1} 条", n, total.value) : t("{0} 条", n);
});
// 整行可点跳实体，但 EP 渲染的 tr 不可聚焦。审计是分页取数的，所以列排序交给后端，这里只补键盘。
const tableRef = ref<TableInstance>();
useKeyboardRows(tableRef, () => shown.value);

/**
 * 排序交给后端：这张表是分页取数的，客户端只能排本页 50 条，会给出错误的结论。
 * 搜索词仍是本页过滤——它要能命中「放行」这类中文动作标签，服务端只会按 code 做 LIKE。
 */
const pg = useServerPaging({ reload: load, defaultSort: "at" });
const { take, total, page, defaultSort, onSortChange, onPageChange, onFilterChange } = pg;

const batchIdFromAudit = /\bbatch=([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\b/;

function labSampleBatchId(row: AuditLogDto): string | null {
  const match = batchIdFromAudit.exec(row.detail ?? "");
  return match?.[1] ?? null;
}

function canOpenEntity(row: AuditLogDto): boolean {
  switch (row.entityType) {
    case "MasterRecipe":
    case "ProductionBatch":
    case "MaterialLot":
    case "EquipmentLine":
    case "PhaseTemplate":
    case "AppUser":
    case "ProcessAlarm":
      return true;
    case "LabSample":
      return !!labSampleBatchId(row);
    default:
      return false;
  }
}

function rowClass({ row }: { row: AuditLogDto }) {
  return canOpenEntity(row) ? "" : "is-dead";
}

function openEntity(row: AuditLogDto) {
  if (!canOpenEntity(row)) {
    ElMessage.info("该记录没有对应页面。");
    return;
  }
  switch (row.entityType) {
    case "MasterRecipe":
      void router.push(`/recipes/${row.entityId}`);
      return;
    case "ProductionBatch":
      void router.push(`/batches/${row.entityId}`);
      return;
    case "MaterialLot":
      void router.push(`/lots/${row.entityId}`);
      return;
    case "EquipmentLine":
    case "PhaseTemplate":
      void router.push("/equipment");
      return;
    case "AppUser":
      void router.push("/users");
      return;
    case "ProcessAlarm":
      void router.push("/alarms");
      return;
    case "LabSample": {
      const batchId = labSampleBatchId(row);
      if (batchId) void router.push(`/batches/${batchId}/record`);
      return;
    }
    default:
      break;
  }
}

async function load() {
  const params: Record<string, string> = pg.params();
  if (entityType.value) params.entityType = entityType.value;
  await run(http.get<AuditLogPageDto>("/audit", { params }), (d) => {
    items.value = d.items;
    total.value = d.total;
  });
}

onMounted(load);
</script>

<style scoped>
:deep(.el-table__body tr.is-dead) { cursor: default; }
</style>
