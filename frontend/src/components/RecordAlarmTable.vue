<template>
  <el-table v-if="rows?.length" :data="rows" size="small" border>
    <el-table-column prop="raisedAt" :label="$t('时间')" width="180" fixed>
      <template #default="{ row }">{{ formatDateTime(row.raisedAt) }}</template>
    </el-table-column>
    <el-table-column prop="stepCode" :label="$t('工步')" width="80" />
    <el-table-column prop="code" :label="$t('代码')" width="140" />
    <el-table-column prop="message" :label="$t('说明')" />
    <el-table-column :label="$t('确认')">
      <template #default="{ row }">{{ row.acknowledgedAt ? row.acknowledgedBy : $t("未确认") }}</template>
    </el-table-column>
  </el-table>
  <p v-else class="none-note">{{ $t("本批次无握手/调度报警。") }}</p>
</template>

<script setup lang="ts">
import type { ProcessAlarmDto } from "../api/types";
import { formatDateTime } from "../utils/format";

/** 本批的过程报警。说明列是报警当时落库的文本，不翻译。 */
defineProps<{ rows?: ProcessAlarmDto[] }>();
</script>
