<template>
  <div class="no-print gap-after-sm">
    <el-button v-if="canTakeSample" size="small" :loading="busy === 'sample'" :disabled="!!busy" @click="emit('sample')">{{ $t("取样") }}</el-button>
  </div>
  <el-table v-if="rows?.length" :data="rows" size="small" border>
    <el-table-column prop="sampleCode" :label="$t('样品号')" width="140" fixed />
    <el-table-column prop="sampleType" :label="$t('类型')" width="100">
      <template #default="{ row }">{{ labSampleTypeLabel(row.sampleType) }}</template>
    </el-table-column>
    <el-table-column prop="lotNumber" :label="$t('物料批')" width="140" />
    <el-table-column prop="disposition" :label="$t('判定')" width="90">
      <template #default="{ row }">{{ labDispositionLabel(row.disposition) }}</template>
    </el-table-column>
    <el-table-column prop="takenBy" :label="$t('取样人')" width="110" />
    <el-table-column prop="comment" :label="$t('意见')" />
    <el-table-column v-if="canDispose" :label="$t('操作')" width="160" class-name="no-print">
      <template #default="{ row }">
        <el-button
          v-if="row.disposition === 'Pending' && quality"
          link
          type="primary"
          :loading="busy === `pass:${row.id}`"
          :disabled="!!busy"
          @click="emit('dispose', row.id, 'Pass')"
        >{{ $t("合格") }}</el-button>
        <el-button
          v-if="row.disposition === 'Pending' && quality"
          link
          type="danger"
          :loading="busy === `fail:${row.id}`"
          :disabled="!!busy"
          @click="emit('dispose', row.id, 'Fail')"
        >{{ $t("不合格") }}</el-button>
      </template>
    </el-table-column>
  </el-table>
  <p v-else class="none-note">{{ $t("本批次无实验室样品。") }}</p>
</template>

<script setup lang="ts">
import { computed } from "vue";
import type { LabSampleDisposition, LabSampleDto } from "../api/types";
import { labDispositionLabel, labSampleTypeLabel } from "../utils/labels";
import { useAuthStore } from "../stores/auth";

/**
 * LIMS 样品区。取样与判定要电子签名、要转圈、要在成功后重拉整页，
 * 那些状态只有父页有（busy 是"同一时刻只允许一个签名动作"的唯一来源），
 * 所以这里只发事件，不自己发请求。`canDispose` 同理由父页算好：它连着放行条件。
 */
defineProps<{ rows?: LabSampleDto[]; busy: string; canDispose: boolean }>();
const emit = defineEmits<{
  sample: [];
  dispose: [sampleId: string, disposition: LabSampleDisposition];
}>();

const auth = useAuthStore();
const quality = computed(() => auth.is("Quality"));
const canTakeSample = computed(() => auth.can("Operator", "Quality", "Supervisor"));
</script>
