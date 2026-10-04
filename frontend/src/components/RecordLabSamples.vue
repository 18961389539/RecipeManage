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
    <el-table-column :label="$t('判定签名校验')" min-width="210">
      <template #default="{ row }">
        <template v-if="row.dispositionSignature">
          <div>{{ row.dispositionSignature.signerName }} · {{ new Date(row.dispositionSignature.at).toLocaleString() }}</div>
          <el-tooltip
            v-if="row.dispositionSignature.contentHash"
            :content="`SHA-256: ${row.dispositionSignature.contentHash}`"
            placement="top"
          >
            <el-tag size="small" :type="integrityTagType(row.dispositionSignature.integrity)">
              {{ integrityLabel(row.dispositionSignature.integrity) }}
            </el-tag>
          </el-tooltip>
          <el-tag v-else size="small" type="warning">
            {{ integrityLabel(row.dispositionSignature.integrity) }}
          </el-tag>
        </template>
        <el-tag v-else-if="row.disposition !== 'Pending'" size="small" type="danger">
          {{ $t("缺少电子签名记录") }}
        </el-tag>
        <span v-else>—</span>
      </template>
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
import { useI18n } from "vue-i18n";
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
const { t } = useI18n();
const quality = computed(() => auth.is("Quality"));
const canTakeSample = computed(() => auth.can("Operator", "Quality", "Supervisor"));
const integrityLabels: Record<string, string> = {
  Verified: "摘要匹配",
  Unbound: "历史签名无摘要",
  Mismatch: "内容不匹配",
  Unsupported: "不支持的摘要版本",
};
const integrityLabel = (status: string) => t(integrityLabels[status] ?? status);
const integrityTagType = (status: string) =>
  status === "Verified" ? "success" : status === "Unbound" ? "warning" : "danger";
</script>
