<template>
  <div>
    <div class="page-title"><h2>操作审计</h2></div>
    <el-alert class="gap-after" type="info" show-icon :closable="false" title="配方提交/审核、批次启停故障、设备点表变更均写入审计。按实体筛选可还原一次闭环。" />
    <el-form inline>
      <el-form-item label="实体">
        <el-select v-model="entityType" clearable placeholder="全部" style="width:180px" @change="onFilterChange">
          <el-option label="主配方" value="MasterRecipe" />
          <el-option label="生产批次" value="ProductionBatch" />
          <el-option label="设备" value="EquipmentLine" />
        </el-select>
      </el-form-item>
      <el-form-item><el-button type="primary" :loading="loading" @click="load">刷新</el-button></el-form-item>
    </el-form>
    <el-alert class="gap-after"
      v-if="error"
      :closable="false"
      type="error"
      :title="`审计记录加载失败：${error}`"
      show-icon
     
    />
    <el-table :data="items" size="small" v-loading="loading" empty-text="暂无符合条件的审计记录">
      <el-table-column prop="at" label="时间" width="200" fixed>
        <template #default="{ row }">{{ formatDateTime(row.at) }}</template>
      </el-table-column>
      <el-table-column prop="userName" label="用户" width="120" />
      <el-table-column prop="action" label="动作" width="180" />
      <el-table-column prop="entityType" label="实体" width="150">
        <template #default="{ row }">{{ auditEntityLabel(row.entityType) }}</template>
      </el-table-column>
      <el-table-column prop="detail" label="详情" />
    </el-table>
    <el-pagination class="pager" layout="total, prev, pager, next" background small :page-size="take"
      :current-page="page" :total="total" hide-on-single-page @current-change="onPageChange" />
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import http from "../../api/http";
import type { AuditLogDto, AuditLogPageDto } from "../../api/types";
import { auditEntityLabel } from "../../utils/labels";
import { formatDateTime } from "../../utils/format";
import { useLoad } from "../../utils/useLoad";

const items = ref<AuditLogDto[]>([]);
const total = ref(0);
const page = ref(1);
const take = 50;
const entityType = ref("");
const { loading, error, run } = useLoad();

async function load() {
  const params: Record<string, string> = { take: String(take), skip: String((page.value - 1) * take) };
  if (entityType.value) params.entityType = entityType.value;
  await run(http.get<AuditLogPageDto>("/audit", { params }), (d) => {
    items.value = d.items;
    total.value = d.total;
  });
}

function onPageChange(p: number) {
  page.value = p;
  void load();
}

/** 换筛选后要回第一页，否则停在旧页码上会显示空白表格，像是筛选没命中。 */
function onFilterChange() {
  page.value = 1;
  void load();
}

onMounted(load);
</script>

<style scoped>
.pager { margin-top: var(--space-3); justify-content: flex-end; }
@media (max-width: 768px) {
  .pager { justify-content: flex-start; }
}
</style>
