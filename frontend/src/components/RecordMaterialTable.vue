<template>
  <el-table v-if="rows?.length" :data="rows" size="small" border>
    <el-table-column prop="role" :label="$t('角色')" width="90" fixed>
      <template #default="{ row }">{{ lotRoleLabel(row.role) }}</template>
    </el-table-column>
    <el-table-column prop="lotNumber" :label="$t('物料批')">
      <template #default="{ row }">
        <el-link type="primary" @click="$router.push(`/lots/${row.materialLotId}`)">{{ row.lotNumber }}</el-link>
      </template>
    </el-table-column>
    <el-table-column prop="materialCode" :label="$t('物料')" width="110" />
    <el-table-column prop="quantity" :label="$t('数量')" width="90" />
  </el-table>
  <p v-else class="none-note">{{ $t("本批次未绑定物料谱系（快照仍可仅有 LotNumber 字符串）。") }}</p>
</template>

<script setup lang="ts">
import type { BatchMaterialUseDto } from "../api/types";
import { lotRoleLabel } from "../utils/labels";

/** 投料与产出谱系。链接跳物料批详情页，审查时要从这批追到上一批的来历。 */
defineProps<{ rows?: BatchMaterialUseDto[] }>();
</script>
