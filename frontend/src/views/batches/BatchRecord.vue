<template>
  <!-- 原先根节点是 v-if="record"：取数失败会留下整页空白且无任何提示。 -->
  <div class="page-state" v-if="error || (!record && loading)">
    <el-alert
      v-if="error"
      type="error"
      show-icon
      :closable="false"
      :title="$t('电子批记录加载失败：{0}', [error])"
      :description="$t('请确认批次是否已执行完成，或返回批次列表重试。')"
    />
    <el-skeleton v-else :rows="8" animated />
  </div>
  <div v-if="record" class="ebr">
    <div class="page-title no-print sticky-actions">
      <div>
        <h2>{{ $t("电子批记录 · {0}", [record.batchNo]) }}<PageGuideButton guide-key="batchRecord" /></h2>
        <span>{{ record.snapshot.recipeName }} v{{ record.snapshot.versionNumber }} · {{ batchStatusLabel(record.status) }} · {{ $t("快照：{0}", [integrityLabel]) }}</span>
      </div>
      <div>
        <el-button @click="backToMonitor">{{ $t("返回监控") }}</el-button>
        <el-button @click="print">{{ $t("打印") }}</el-button>
        <el-button type="primary" :loading="busy === 'pdf'" @click="downloadPdf">{{ $t("导出 PDF/A") }}</el-button>
        <HelpTip v-if="canRelease" term="质量放行" chord="ctrl+enter" allow-in-input plain placement="bottom">
          <el-button type="success" :loading="busy === 'release'" :disabled="!!busy" @click="releaseLot">{{ $t("质量放行") }}</el-button>
        </HelpTip>
        <HelpTip v-if="canRelease" term="质量拒收" plain placement="bottom">
          <el-button type="danger" :loading="busy === 'reject'" :disabled="!!busy" @click="rejectLot">{{ $t("质量拒收") }}</el-button>
        </HelpTip>
      </div>
    </div>

    <el-alert
      class="no-print gap-after"
      :closable="false"
      type="info"
      show-icon
      :title="$t('本页归档控制配方快照、ISA-88 工步、四步握手时序、实测质检与配方/批次电子签名含义。执行完成后由质量电子签名放行或拒收。')"
    />

    <RecordSection :title="$t('批次抬头')">
      <RecordBatchHeader :record="record" :integrity-label="integrityLabel" :unit-equipment-text="unitEquipmentText" />
    </RecordSection>

    <RecordSection :title="$t('物料投料与产出谱系')">
      <RecordMaterialTable :rows="record.materials" />
    </RecordSection>

    <RecordSection :title="$t('配方电子签名')">
      <RecordApprovalsTable :rows="record.recipeApprovals" />
    </RecordSection>

    <RecordSection :title="$t('批次执行电子签名')">
      <RecordEsignTable :rows="record.esigns" />
    </RecordSection>

    <RecordSection :title="$t('过程报警')">
      <RecordAlarmTable :rows="record.alarms" />
    </RecordSection>

    <RecordSection
      :title="$t('ISA-88 控制配方（快照）')"
      :hint="$t('整张工艺 → 单元规程 → 操作 → 工步。下图画布与监控页同源，按冻结连线排布。')"
    >
      <RecordProcedurePanel
        :steps="record.snapshot.steps"
        :edges="flowEdges"
        :flow-steps="flowSteps"
        :outcomes="flowOutcomes"
        :matrix-steps="matrixSteps"
        :measured="measured"
        :write-plan="record.writePlan ?? []"
      />
    </RecordSection>

    <RecordSection :title="$t('实验室样品（LIMS，与 PLC 测点分开）')">
      <RecordLabSamples
        :rows="record.labSamples"
        :busy="busy"
        :can-dispose="canRelease || record.status === 'Completed'"
        @sample="takeSample"
        @dispose="disposeSample"
      />
    </RecordSection>

    <RecordSection :title="$t('归档质检')">
      <RecordQualityTable :rows="qualityRows" />
    </RecordSection>

    <RecordSection :title="$t('四步握手时序（禁止盲写）')">
      <RecordHandshakeTable :rows="record.handshake" />
    </RecordSection>

    <RecordSection :title="$t('快照 vs 当前生效主配方')">
      <RecordDriftTable :rows="record.drift" />
    </RecordSection>
  </div>
</template>

<script setup lang="ts">
import { t } from "../../i18n";
import { computed, onMounted, ref } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessage, ElMessageBox } from "element-plus";
import http from "../../api/http";
import { esignWithReason } from "../../utils/esign";
import { useLoad } from "../../utils/useLoad";
import { useBatchSnapshotView } from "../../utils/useBatchSnapshotView";
import { batchStatusLabel, esignMeaning } from "../../utils/labels";
import type { BatchRecordDto, EquipmentDto, LabSampleDisposition } from "../../api/types";
import HelpTip from "../../components/HelpTip.vue";
import RecordSection from "../../components/RecordSection.vue";
import RecordBatchHeader from "../../components/RecordBatchHeader.vue";
import RecordMaterialTable from "../../components/RecordMaterialTable.vue";
import RecordApprovalsTable from "../../components/RecordApprovalsTable.vue";
import RecordEsignTable from "../../components/RecordEsignTable.vue";
import RecordAlarmTable from "../../components/RecordAlarmTable.vue";
import RecordProcedurePanel from "../../components/RecordProcedurePanel.vue";
import RecordLabSamples from "../../components/RecordLabSamples.vue";
import RecordQualityTable from "../../components/RecordQualityTable.vue";
import RecordHandshakeTable from "../../components/RecordHandshakeTable.vue";
import RecordDriftTable from "../../components/RecordDriftTable.vue";
import { usePageShortcuts } from "../../shortcuts/registry";
import { useAuthStore } from "../../stores/auth";

/**
 * 电子批记录（eBR）：一个批次的归档凭据。
 *
 * 本页只负责三件事——取数、放行/拒收/取样这类要电子签名的动作、以及打印排版；
 * 每个归档区块拆成 Record* 子组件，快照的展示投影与监控页共用 useBatchSnapshotView，
 * 这样同一批次在两页上不可能给出两套结论。
 */
const route = useRoute();
const router = useRouter();
const auth = useAuthStore();
const record = ref<BatchRecordDto | null>(null);
const busy = ref("");
const equipmentIndex = ref<Record<string, string>>({});

const canRelease = computed(() =>
  !!record.value && record.value.status === "Completed" && auth.is("Quality"));

// 归档页没有"选中工步"这回事：设定矩阵与归档质检都要全量，pickedStep 恒空。
const noPickedStep = ref<string | null>(null);
const {
  flowSteps, flowEdges, flowOutcomes, matrixSteps, measured, qualityRows, integrityLabel
} = useBatchSnapshotView(record, noPickedStep, equipmentIndex);

const unitEquipmentText = computed(() => {
  const map = record.value?.snapshot.unitEquipment;
  if (!map || Object.keys(map).length === 0) return t("全部使用主设备");
  return Object.entries(map)
    .map(([unit, id]) => `${unit} → ${equipmentIndex.value[id] ?? id.slice(0, 8)}`)
    .join("；");
});

function print() {
  window.print();
}

async function downloadPdf() {
  if (!record.value) return;
  // 归档件按批次全量取数渲染，慢的时候几秒没有反馈。
  busy.value = "pdf";
  try {
    const res = await http.get(`/batches/${record.value.batchId}/record.pdf`, { responseType: "blob" });
    const url = URL.createObjectURL(res.data as Blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `${record.value.batchNo}-eBR.pdf`;
    a.click();
    URL.revokeObjectURL(url);
  } catch (e) {
    ElMessage.error((e as Error).message ?? t("导出 PDF 失败"));
  } finally {
    busy.value = "";
  }
}

function backToMonitor() {
  if (record.value)
    router.push(`/batches/${record.value.batchId}`);
}

async function takeSample() {
  if (!record.value || busy.value) return;
  busy.value = "sample";
  try {
    const { value: code } = await ElMessageBox.prompt(t("样品编号"), t("实验室取样"));
    await http.post(`/batches/${record.value.batchId}/lab-samples`, {
      sampleCode: code,
      sampleType: "Final",
      materialLotId: record.value.materials?.find((m) => m.role === "Produced")?.materialLotId
        ?? record.value.materials?.[0]?.materialLotId
        ?? null
    });
    ElMessage.success(t("已登记样品"));
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    busy.value = "";
  }
}

async function disposeSample(sampleId: string, disposition: LabSampleDisposition) {
  if (!record.value || busy.value) return;
  busy.value = disposition === "Fail" ? `fail:${sampleId}` : `pass:${sampleId}`;
  try {
    // 与审核台 / 监控页同一个双栏签名框：意见与密码一屏填完，不再第二屏只问密码。
    const { reason: comment, password } = await esignWithReason(
      t("样品判定"),
      esignMeaning("lab.sample.dispose.esign"),
      t("判定意见"),
      disposition === "Fail",
      disposition === "Fail" ? t("不合格必须填写对照规格的意见。") : t("请填写判定意见（可空）。")
    );
    await http.post(`/batches/${record.value.batchId}/lab-samples/${sampleId}/disposition`, {
      password,
      disposition,
      comment
    });
    ElMessage.success(t("样品已判定"));
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    busy.value = "";
  }
}

async function releaseLot() {
  if (!record.value || busy.value) return;
  busy.value = "release";
  try {
    const { reason, password } = await esignWithReason(
      t("质量放行"),
      esignMeaning("batch.release.esign"),
      t("放行意见"),
      false,
      t("请对照归档质检与四步握手填写放行意见。超差时必须说明偏差放行理由。"));
    await http.post(`/batches/${record.value.batchId}/release`, {
      password,
      reason,
      evidenceHashVersion: record.value.evidenceHashVersion,
      evidenceHash: record.value.evidenceHash
    });
    ElMessage.success(t("批次已质量放行"));
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    busy.value = "";
  }
}

async function rejectLot() {
  if (!record.value || busy.value) return;
  busy.value = "reject";
  try {
    const { reason, password } = await esignWithReason(
      t("质量拒收"),
      esignMeaning("batch.reject.esign"),
      t("拒收意见"),
      true,
      t("请填写拒收意见（对照质检超差或握手异常）。"));
    await http.post(`/batches/${record.value.batchId}/reject-disposition`, {
      password,
      reason,
      evidenceHashVersion: record.value.evidenceHashVersion,
      evidenceHash: record.value.evidenceHash
    });
    ElMessage.success(t("批次已质量拒收"));
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    busy.value = "";
  }
}

usePageShortcuts(() => [
  {
    id: "record.release",
    chord: "ctrl+enter",
    group: "电子批记录",
    label: "质量放行",
    allowInInput: true,
    when: () => canRelease.value && !busy.value,
    run: () => { void releaseLot(); }
  }
]);

const { loading, error } = useLoad();

async function load() {
  try {
    record.value = (await http.get<BatchRecordDto>(`/batches/${route.params.id}/record`)).data;
    equipmentIndex.value = Object.fromEntries(
      (await http.get<EquipmentDto[]>("/equipment")).data.map((e) => [e.id, e.code]));
    error.value = "";
  } catch (e) {
    // 原先取数失败会留下空白页且无任何提示（根节点 v-if="record"），这里显式报错。
    error.value = (e as Error).message || t("电子批记录加载失败");
  } finally {
    loading.value = false;
  }
}

onMounted(load);
</script>

<style scoped>
/* .ebr-block / .none-note / .no-print 都在 styles.css 全站层里：
   区块样式属于 RecordSection，而 slot 内容带的是父作用域标记，scoped 规则够不到。 */
@media print {
  /* 打印时纸是白的（浏览器默认不打印背景），而 Element Plus 的表格文字仍取暗色主题的浅色，
     实测 .cell 计算值 rgb(201,215,242) —— 白纸上等于隐形。原先只写了 .ebr{color:#111}，
     它会被组件自己的 color 覆盖。改令牌不改选择器：EP 组件的文字/边框色全部走这些变量。
     子组件不用各自处理：CSS 自定义属性会继承进子组件的表格里。 */
  .ebr {
    color: #111;
    /* 全站自己的表头规则用的是 var(--muted)（styles.css 的 .el-table th.el-table__cell），
       只改 EP 令牌压不住它——这几个令牌也在 .ebr 子树里一起翻成纸面墨色。 */
    --text: #111;
    --text-body: #1c1c1c;
    --muted: #555;
    --el-text-color-primary: #111;
    --el-text-color-regular: #1c1c1c;
    --el-text-color-secondary: #555;
    --el-text-color-placeholder: #777;
    --el-table-text-color: #111;
    --el-table-header-text-color: #111;
    --el-table-border-color: #c9c9c9;
    --el-table-tr-bg-color: #fff;
    --el-table-header-bg-color: #f4f4f4;
    --el-border-color: #c9c9c9;
    --el-fill-color-light: #f4f4f4;
  }
}
</style>
