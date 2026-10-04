<template>
  <el-descriptions :column="descColumn" border size="small">
    <el-descriptions-item :label="$t('批次号')">{{ record.batchNo }}</el-descriptions-item>
    <el-descriptions-item :label="$t('状态')">{{ batchStatusLabel(record.status) }}</el-descriptions-item>
    <el-descriptions-item :label="$t('快照完整性')">{{ integrityLabel }}</el-descriptions-item>
    <el-descriptions-item :label="$t('主配方')">{{ record.snapshot.recipeCode }} {{ record.snapshot.recipeName }}</el-descriptions-item>
    <el-descriptions-item :label="$t('版本')">v{{ record.snapshot.versionNumber }}</el-descriptions-item>
    <el-descriptions-item :label="$t('冻结时间')">{{ formatDateTime(record.snapshot.frozenAt) }}</el-descriptions-item>
    <el-descriptions-item :label="$t('产品')">{{ record.snapshot.productCode }} {{ record.snapshot.productName }}</el-descriptions-item>
    <el-descriptions-item :label="$t('缩放因子')">{{ record.snapshot.scaleFactor ?? 1 }}</el-descriptions-item>
    <el-descriptions-item :label="$t('物料批次')">{{ record.snapshot.lotNumber || "—" }}</el-descriptions-item>
    <el-descriptions-item :label="$t('放行人')">{{ record.releasedBy || $t("待放行") }}</el-descriptions-item>
    <el-descriptions-item :label="$t('放行时间')">{{ formatDateTime(record.releasedAt) }}</el-descriptions-item>
    <el-descriptions-item :label="$t('放行意见')" :span="3">{{ record.releaseComment || "—" }}</el-descriptions-item>
    <el-descriptions-item :label="$t('处置证据摘要')" :span="3">
      <el-tooltip v-if="record.evidenceHash" :content="record.evidenceHash" placement="top">
        <code>v{{ record.evidenceHashVersion }} · {{ record.evidenceHash.slice(0, 16) }}…</code>
      </el-tooltip>
      <span v-else>—</span>
    </el-descriptions-item>
    <el-descriptions-item :label="$t('单元设备')" :span="3">{{ unitEquipmentText }}</el-descriptions-item>
  </el-descriptions>
</template>

<script setup lang="ts">
import { computed } from "vue";
import type { BatchRecordDto } from "../api/types";
import { batchStatusLabel } from "../utils/labels";
import { formatDateTime } from "../utils/format";
import { useIsMobile } from "../utils/useMedia";

/**
 * 批次抬头：这批"当时是什么"的唯一凭据，字段全部来自落库的快照与放行记录。
 *
 * 完整性标签与单元设备绑定要跨表查设备编码，父页已经取过设备清单，
 * 所以这两格由父算好传进来，这里只负责排版——不在组件里再发一次请求。
 */
defineProps<{
  record: BatchRecordDto;
  integrityLabel: string;
  unitEquipmentText: string;
}>();

// 抬头字段是「标签+值」的表格，手机上三列并排会把每格压到放不下一个中文词
const isMobile = useIsMobile();
const descColumn = computed(() => (isMobile.value ? 1 : 3));
</script>
