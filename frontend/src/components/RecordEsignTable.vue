<template>
  <el-table v-if="rows?.length" :data="rows" size="small" border>
    <el-table-column prop="action" :label="$t('动作')" width="150" fixed>
      <template #default="{ row }">{{ auditActionLabel(row.action) }}</template>
    </el-table-column>
    <el-table-column prop="userName" :label="$t('签署人')" width="120" />
    <el-table-column :label="$t('时间')" width="180">
      <template #default="{ row }">{{ formatDateTime(row.at) }}</template>
    </el-table-column>
    <el-table-column prop="meaning" :label="$t('签署含义')" min-width="240" />
    <el-table-column prop="extra" :label="$t('意见')" />
    <el-table-column :label="$t('证据摘要校验')" min-width="180">
      <template #default="{ row }">
        <template v-if="row.integrity">
          <el-tag size="small" :type="integrityTagType(row.integrity)">
            {{ $t(integrityLabel(row.integrity)) }}
          </el-tag>
          <el-tooltip v-if="row.contentHash" :content="row.contentHash" placement="top">
            <code class="hash-prefix">{{ row.contentHash.slice(0, 12) }}…</code>
          </el-tooltip>
        </template>
        <span v-else>—</span>
      </template>
    </el-table-column>
  </el-table>
  <p v-else class="none-note">{{ $t("尚无启动 / 保持 / 跳步 / 放行签署（旧批次可在审计日志查看动作码）。") }}</p>
</template>

<script setup lang="ts">
import type { BatchEsignDto } from "../api/types";
import { auditActionLabel } from "../utils/labels";
import { formatDateTime } from "../utils/format";

/** 批次执行期的电子签名（启动/保持/跳步/放行…）。含义列是签署当时写入的原文，不翻译。 */
defineProps<{ rows?: BatchEsignDto[] }>();
const integrityLabels: Record<NonNullable<BatchEsignDto["integrity"]>, string> = {
  Verified: "摘要匹配",
  Unbound: "历史签名无摘要",
  Mismatch: "内容不匹配",
  Unsupported: "不支持的摘要版本",
};
const integrityLabel = (status: BatchEsignDto["integrity"]) =>
  status ? integrityLabels[status] : "";
const integrityTagType = (status: NonNullable<BatchEsignDto["integrity"]>) =>
  status === "Verified" ? "success" : status === "Unbound" ? "warning" : "danger";
</script>
