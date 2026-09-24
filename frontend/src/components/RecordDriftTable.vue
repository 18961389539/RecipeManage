<template>
  <el-table :data="rows" size="small" border>
    <el-table-column prop="stepCode" :label="$t('工步')" width="80" fixed />
    <el-table-column prop="parameter" :label="$t('参数')" />
    <el-table-column prop="frozenSetpoint" :label="$t('快照设定')" width="110" />
    <el-table-column prop="masterSetpoint" :label="$t('主配方')" width="110" />
    <el-table-column :label="$t('漂移')" width="80">
      <template #default="{ row }">{{ $t(row.drifted ? "是" : "否") }}</template>
    </el-table-column>
  </el-table>
</template>

<script setup lang="ts">
import type { SnapshotDriftDto } from "../api/types";

/**
 * 快照 vs 当前生效主配方的漂移表：主配方升版后，本批仍按冻结值执行，
 * 这张表就是"当时到底按哪套参数跑"的证据，因此值一律取落库快照，不做实时换算。
 */
defineProps<{ rows: SnapshotDriftDto[] }>();
</script>
