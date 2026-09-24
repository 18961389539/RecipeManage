<template>
  <el-table v-if="rows.length" :data="rows" size="small" border>
    <el-table-column prop="step" :label="$t('工步')" width="80" fixed />
    <el-table-column :label="$t('测点')">
      <template #default="{ row }">{{ signalLabel(row.tag) }}</template>
    </el-table-column>
    <el-table-column prop="value" :label="$t('实测')" width="100" />
    <el-table-column prop="spec" :label="$t('规格')" />
    <el-table-column :label="$t('判定')" width="80">
      <template #default="{ row }">
        <span :style="{ color: row.oos ? 'var(--err)' : 'var(--ok)' }">{{ row.oos ? $t("超差") : $t("合格") }}</span>
      </template>
    </el-table-column>
  </el-table>
  <p v-else class="none-note">{{ $t("尚无工步归档质检。") }}</p>
</template>

<script setup lang="ts">
import { signalLabel } from "../utils/labels";

/**
 * 归档质检：设定值 + 规格窗口 + 当时实测，来自快照与工步执行记录，
 * 由 useBatchSnapshotView 的 qualityReadings 投影算好传来（与监控页同一口径）。
 */
defineProps<{ rows: { step: string; tag: string; value: string; spec: string; oos: boolean }[] }>();
</script>
