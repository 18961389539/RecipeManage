<template>
  <el-table :data="rows" size="small" border>
    <el-table-column prop="title" :label="$t('审核节点')" width="140" fixed>
      <template #default="{ row }">{{ row.title || approvalNodeLabel(row.node) }}</template>
    </el-table-column>
    <el-table-column :label="$t('要求角色')" width="110">
      <template #default="{ row }">{{ userRoleLabel(row.requiredRole) }}</template>
    </el-table-column>
    <el-table-column :label="$t('结论')" width="110">
      <template #default="{ row }">{{ approvalDecisionLabel(row.decision) }}</template>
    </el-table-column>
    <el-table-column prop="reviewerName" :label="$t('签署人')" />
    <!-- 日期时间 19 字符实测 131px；无定宽时被前面的固定列挤到 119px、折成两行。
         与全站日期列统一按 176 定宽。 -->
    <el-table-column :label="$t('时间')" width="176" show-overflow-tooltip>
      <template #default="{ row }">{{ formatDateTime(row.decidedAt) }}</template>
    </el-table-column>
    <el-table-column prop="meaning" :label="$t('签署含义')" min-width="220" />
    <!-- comment 是自由意见，但操作员常把批次号写进来（16 位 = 132px）。
         定宽不足时批次号折行；min-width 保底 + 省略号兜底。 -->
    <el-table-column prop="comment" :label="$t('意见')" min-width="160" show-overflow-tooltip />
  </el-table>
</template>

<script setup lang="ts">
import type { ApprovalDto } from "../api/types";
import { approvalDecisionLabel, approvalNodeLabel, userRoleLabel } from "../utils/labels";
import { formatDateTime } from "../utils/format";

/**
 * 配方版本的审批签署链。节点名与签署含义都是**提交时冻结的原文**，
 * 这里只翻译列头与枚举标签，绝不改动落库文本（21 CFR 11 要求痕迹不可事后改写）。
 */
defineProps<{ rows: ApprovalDto[] }>();
</script>
