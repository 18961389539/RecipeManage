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
  </el-table>
  <p v-else class="none-note">{{ $t("尚无启动 / 保持 / 跳步 / 放行签署（旧批次可在审计日志查看动作码）。") }}</p>
</template>

<script setup lang="ts">
import type { BatchEsignDto } from "../api/types";
import { auditActionLabel } from "../utils/labels";
import { formatDateTime } from "../utils/format";

/** 批次执行期的电子签名（启动/保持/跳步/放行…）。含义列是签署当时写入的原文，不翻译。 */
defineProps<{ rows?: BatchEsignDto[] }>();
</script>
