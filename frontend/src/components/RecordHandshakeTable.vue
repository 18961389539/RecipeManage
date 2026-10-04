<template>
  <el-table v-if="rows?.length" :data="rows" size="small" border max-height="420">
    <el-table-column :label="$t('时间')" width="180" fixed>
      <template #default="{ row }">{{ formatDateTime(row.at) }}</template>
    </el-table-column>
    <el-table-column prop="stepCode" :label="$t('工步')" width="80" />
    <el-table-column :label="$t('阶段')" width="140">
      <template #default="{ row }">{{ handshakePhaseLabel(row.phase) }}</template>
    </el-table-column>
    <el-table-column :label="$t('动作')" width="80">
      <template #default="{ row }">{{ handshakeKindLabel(row.kind) }}</template>
    </el-table-column>
    <el-table-column prop="detail" :label="$t('说明')" />
  </el-table>
  <p v-else class="none-note">{{ $t("本批次尚无握手事件（未启动，或未产生过合法动作）。") }}</p>
</template>

<script setup lang="ts">
import type { HandshakeLogDto } from "../api/types";
import { handshakeKindLabel, handshakePhaseLabel } from "../utils/labels";
import { formatDateTime } from "../utils/format";

/**
 * 四步握手时序。说明列是握手事件落库的原文——审查要核对"当时写了什么、PLC 回了什么"，
 * 读取时再翻译会让同一份批记录在不同人屏幕上显示不同内容，所以只译列头与枚举标签。
 */
defineProps<{ rows: HandshakeLogDto[] }>();
</script>
