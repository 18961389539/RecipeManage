<template>
  <!-- 原先根节点是 v-if="record"：取数失败会留下整页空白且无任何提示。 -->
  <div class="page-state" v-if="error || (!record && loading)">
    <el-alert
      v-if="error"
      type="error"
      show-icon
      :closable="false"
      :title="`电子批记录加载失败：${error}`"
      description="请确认批次是否已执行完成，或返回批次列表重试。"
    />
    <el-skeleton v-else :rows="8" animated />
  </div>
  <div v-if="record" class="ebr">
    <div class="page-title no-print">
      <div>
        <h2>电子批记录 · {{ record.batchNo }}</h2>
        <span>{{ record.snapshot.recipeName }} v{{ record.snapshot.versionNumber }} · {{ batchStatusLabel(record.status) }} · 快照 {{ integrityLabel }}</span>
      </div>
      <div>
        <el-button @click="backToMonitor">返回监控</el-button>
        <el-button @click="print">打印</el-button>
        <el-button type="primary" :loading="busy === 'pdf'" @click="downloadPdf">导出 PDF/A</el-button>
        <el-button v-if="canRelease" type="success" @click="releaseLot">质量放行</el-button>
        <el-button v-if="canRelease" type="danger" @click="rejectLot">质量拒收</el-button>
      </div>
    </div>

    <el-alert
      class="no-print gap-after"
      :closable="false"
      type="info"
      show-icon
      title="本页归档控制配方快照、ISA-88 工步、四步握手时序、实测质检与配方电子签名含义。执行完成后由质量电子签名放行或拒收。"
     
    />

    <section class="block">
      <h3>批次抬头</h3>
      <el-descriptions :column="descColumn" border size="small">
        <el-descriptions-item label="批次号">{{ record.batchNo }}</el-descriptions-item>
        <el-descriptions-item label="状态">{{ batchStatusLabel(record.status) }}</el-descriptions-item>
        <el-descriptions-item label="快照完整性">{{ integrityLabel }}</el-descriptions-item>
        <el-descriptions-item label="主配方">{{ record.snapshot.recipeCode }} {{ record.snapshot.recipeName }}</el-descriptions-item>
        <el-descriptions-item label="版本">v{{ record.snapshot.versionNumber }}</el-descriptions-item>
        <el-descriptions-item label="冻结时间">{{ formatTime(record.snapshot.frozenAt) }}</el-descriptions-item>
        <el-descriptions-item label="产品">{{ record.snapshot.productCode }} {{ record.snapshot.productName }}</el-descriptions-item>
        <el-descriptions-item label="缩放因子">{{ record.snapshot.scaleFactor ?? 1 }}</el-descriptions-item>
        <el-descriptions-item label="物料批次">{{ record.snapshot.lotNumber || "—" }}</el-descriptions-item>
        <el-descriptions-item label="放行人">{{ record.releasedBy || "待放行" }}</el-descriptions-item>
        <el-descriptions-item label="放行时间">{{ formatTime(record.releasedAt) }}</el-descriptions-item>
        <el-descriptions-item label="放行意见" :span="3">{{ record.releaseComment || "—" }}</el-descriptions-item>
        <el-descriptions-item label="单元设备" :span="3">{{ unitEquipmentText }}</el-descriptions-item>
      </el-descriptions>
    </section>

    <section class="block">
      <h3>物料投料与产出谱系</h3>
      <el-table v-if="record.materials?.length" :data="record.materials" size="small" border>
        <el-table-column prop="role" label="角色" width="90" fixed>
          <template #default="{ row }">{{ ({ Charge: "投料", Produced: "产出", Rework: "返工" } as Record<string, string>)[row.role] ?? row.role }}</template>
        </el-table-column>
        <el-table-column prop="lotNumber" label="物料批">
          <template #default="{ row }">
            <el-link type="primary" @click="$router.push(`/lots/${row.materialLotId}`)">{{ row.lotNumber }}</el-link>
          </template>
        </el-table-column>
        <el-table-column prop="materialCode" label="物料" width="110" />
        <el-table-column prop="quantity" label="数量" width="90" />
      </el-table>
      <el-empty v-else description="本批次未绑定物料谱系（快照仍可仅有 LotNumber 字符串）" />
    </section>

    <section class="block">
      <h3>配方电子签名</h3>
      <el-table :data="record.recipeApprovals" size="small" border>
        <el-table-column prop="level" label="审核级" width="120" fixed>
          <template #default="{ row }">{{ ({ Author: "提交", Supervisor: "工艺主管", Quality: "质量" } as Record<string, string>)[row.level] ?? row.level }}</template>
        </el-table-column>
        <el-table-column prop="decision" label="结论" width="110" />
        <el-table-column prop="reviewerName" label="签署人" />
        <el-table-column label="时间">
          <template #default="{ row }">{{ formatTime(row.decidedAt) }}</template>
        </el-table-column>
        <el-table-column prop="meaning" label="签署含义" min-width="220" />
        <el-table-column prop="comment" label="意见" />
      </el-table>
    </section>

    <section class="block">
      <h3>过程报警</h3>
      <el-table v-if="record.alarms?.length" :data="record.alarms" size="small" border>
        <el-table-column prop="raisedAt" label="时间" width="180" fixed>
          <template #default="{ row }">{{ formatTime(row.raisedAt) }}</template>
        </el-table-column>
        <el-table-column prop="stepCode" label="工步" width="80" />
        <el-table-column prop="code" label="代码" width="140" />
        <el-table-column prop="message" label="说明" />
        <el-table-column label="确认">
          <template #default="{ row }">{{ row.acknowledgedAt ? row.acknowledgedBy : "未确认" }}</template>
        </el-table-column>
      </el-table>
      <el-empty v-else description="本批次无握手/调度报警" />
    </section>

    <section class="block">
      <h3>ISA-88 控制配方（快照）</h3>
      <p class="muted">Procedure（整张工艺）→ Unit Procedure → Operation → Phase（工步）。下图画布与监控页同源，按冻结 Edges 排布。</p>
      <ProcedureFlow
        flow-id="batch-record-flow"
        :steps="flowSteps"
        :edges="record.snapshot.edges ?? []"
        :outcomes="flowOutcomes"
        :height="260"
      />
      <el-table :data="record.snapshot.steps" size="small" border>
        <el-table-column prop="code" label="Phase" width="80" fixed />
        <el-table-column prop="name" label="工步" />
        <el-table-column prop="unitProcedure" label="Unit Procedure" min-width="140" />
        <el-table-column prop="operation" label="Operation" min-width="140" />
        <el-table-column prop="type" label="类型" width="120" />
        <el-table-column label="结果" width="110">
          <template #default="{ row }">{{ stepOutcomeLabel(outcome(row.stepId)) }}</template>
        </el-table-column>
        <el-table-column label="设定值">
          <template #default="{ row }">
            {{ row.parameters.map((p: SnapshotParameter) => `${p.name}=${p.setpoint}${p.engineeringUnit}`).join("；") }}
          </template>
        </el-table-column>
      </el-table>
      <h4>冻结 Setpoints 矩阵（设定 / 归档实测）</h4>
      <SetpointMatrix
        :steps="matrixSteps"
        :measured="measured"
        readonly
        :max-height="280"
      />
      <h4>PLC 写参计划（拓扑顺序，禁止盲写）</h4>
      <PlcWritePlan :items="record.writePlan ?? []" />
    </section>

    <section class="block">
      <h3>实验室样品（LIMS，与 PLC 测点分开）</h3>
      <div class="no-print gap-after-sm">
        <el-button v-if="auth.can('Operator', 'Quality', 'Supervisor')" size="small" @click="takeSample">取样</el-button>
      </div>
      <el-table v-if="record.labSamples?.length" :data="record.labSamples" size="small" border>
        <el-table-column prop="sampleCode" label="样品号" width="140" fixed />
        <el-table-column prop="sampleType" label="类型" width="100">
          <template #default="{ row }">{{ ({ InProcess: "过程", Final: "终检", Retain: "留样" } as Record<string, string>)[row.sampleType] ?? row.sampleType }}</template>
        </el-table-column>
        <el-table-column prop="lotNumber" label="物料批" width="140" />
        <el-table-column prop="disposition" label="判定" width="90">
          <template #default="{ row }">{{ ({ Pending: "待检", Pass: "合格", Fail: "不合格", Void: "作废" } as Record<string, string>)[row.disposition] ?? row.disposition }}</template>
        </el-table-column>
        <el-table-column prop="takenBy" label="取样人" width="110" />
        <el-table-column prop="comment" label="意见" />
        <el-table-column v-if="canRelease || record.status === 'Completed'" label="" width="160" class-name="no-print">
          <template #default="{ row }">
            <el-button
              v-if="row.disposition === 'Pending' && auth.can('Quality')"
              link
              type="primary"
              @click="disposeSample(row.id, 'Pass')"
            >合格</el-button>
            <el-button
              v-if="row.disposition === 'Pending' && auth.can('Quality')"
              link
              type="danger"
              @click="disposeSample(row.id, 'Fail')"
            >不合格</el-button>
          </template>
        </el-table-column>
      </el-table>
      <el-empty v-else description="本批次无实验室样品" />
    </section>

    <section class="block">
      <h3>归档质检</h3>
      <el-table v-if="qualityRows.length" :data="qualityRows" size="small" border>
        <el-table-column prop="step" label="工步" width="80" fixed />
        <el-table-column prop="tag" label="测点" />
        <el-table-column prop="value" label="实测" width="100" />
        <el-table-column prop="spec" label="规格" />
        <el-table-column label="判定" width="80">
          <template #default="{ row }">
            <span :style="{ color: row.oos ? 'var(--err)' : 'var(--ok)' }">{{ row.oos ? "超差" : "合格" }}</span>
          </template>
        </el-table-column>
      </el-table>
      <el-empty v-else description="尚无工步归档质检" />
    </section>

    <section class="block">
      <h3>四步握手时序（禁止盲写）</h3>
      <el-table :data="record.handshake" size="small" border max-height="420">
        <el-table-column label="时间" width="180" fixed>
          <template #default="{ row }">{{ formatTime(row.at) }}</template>
        </el-table-column>
        <el-table-column prop="stepCode" label="工步" width="80" />
        <el-table-column prop="phase" label="阶段" width="160" />
        <el-table-column prop="kind" label="动作" width="110" />
        <el-table-column prop="detail" label="说明" />
      </el-table>
    </section>

    <section class="block">
      <h3>快照 vs 当前生效主配方</h3>
      <el-table :data="record.drift" size="small" border>
        <el-table-column prop="stepCode" label="工步" width="80" fixed />
        <el-table-column prop="parameter" label="参数" />
        <el-table-column prop="frozenSetpoint" label="快照设定" width="110" />
        <el-table-column prop="masterSetpoint" label="主配方" width="110" />
        <el-table-column label="漂移" width="80">
          <template #default="{ row }">{{ row.drifted ? "是" : "否" }}</template>
        </el-table-column>
      </el-table>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessage, ElMessageBox } from "element-plus";
import http from "../../api/http";
import { esignPassword } from "../../utils/esign";
import { useLoad } from "../../utils/useLoad";
import { useIsMobile } from "../../utils/useMedia";
import { snapshotIntegrityLabel } from "../../utils/integrity";
import { batchStatusLabel, stepOutcomeLabel } from "../../utils/labels";
import type { BatchRecordDto, EquipmentDto, LabSampleDisposition, SnapshotParameter } from "../../api/types";
import ProcedureFlow from "../../components/ProcedureFlow.vue";
import SetpointMatrix from "../../components/SetpointMatrix.vue";
import PlcWritePlan from "../../components/PlcWritePlan.vue";
import { snapshotToMatrixSteps, measuredFromExecutions, qualityReadings, formatReadingSpec, formatReadingValue } from "../../setpointMatrix";
import { useAuthStore } from "../../stores/auth";

const route = useRoute();
const router = useRouter();
const auth = useAuthStore();
// 抬头字段是「标签+值」的表格，手机上三列并排会把每格压到放不下一个中文词
const isMobile = useIsMobile();
const descColumn = computed(() => (isMobile.value ? 1 : 3));
const record = ref<BatchRecordDto | null>(null);
const busy = ref("");
const equipmentIndex = ref<Record<string, string>>({});

const canRelease = computed(() =>
  !!record.value && record.value.status === "Completed" && auth.can("Quality"));

const integrityLabel = computed(() => snapshotIntegrityLabel(record.value?.snapshotIntegrity));

const unitEquipmentText = computed(() => {
  const map = record.value?.snapshot.unitEquipment;
  if (!map || Object.keys(map).length === 0) return "全部使用主设备";
  return Object.entries(map)
    .map(([unit, id]) => `${unit} → ${equipmentIndex.value[id] ?? id.slice(0, 8)}`)
    .join("；");
});

function outcome(stepId: string) {
  return record.value?.stepExecutions.find((s) => s.stepId === stepId)?.outcome ?? "Pending";
}

const flowSteps = computed(() =>
  (record.value?.snapshot.steps ?? []).map((s) => ({
    id: s.stepId,
    code: s.code,
    name: s.name,
    type: s.type,
    unitProcedure: s.unitProcedure,
    operation: s.operation,
    ordinal: s.ordinal
  }))
);
const flowOutcomes = computed(() =>
  Object.fromEntries((record.value?.stepExecutions ?? []).map((e) => [e.stepId, e.outcome]))
);
const matrixSteps = computed(() => snapshotToMatrixSteps(record.value?.snapshot.steps ?? []));
const measured = computed(() =>
  measuredFromExecutions(matrixSteps.value, record.value?.stepExecutions ?? [])
);

function formatTime(value?: string | null) {
  if (!value) return "—";
  return new Date(value).toLocaleString();
}

const qualityRows = computed(() =>
  qualityReadings(matrixSteps.value, record.value?.stepExecutions ?? []).map((r) => ({
    step: r.stepCode,
    tag: r.tag,
    value: formatReadingValue(r),
    spec: formatReadingSpec(r),
    oos: r.oos
  }))
);

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
    ElMessage.error((e as Error).message ?? "导出 PDF 失败");
  } finally {
    busy.value = "";
  }
}

function backToMonitor() {
  if (record.value)
    router.push(`/batches/${record.value.batchId}`);
}

async function takeSample() {
  if (!record.value) return;
  try {
    const { value: code } = await ElMessageBox.prompt("样品编号", "实验室取样");
    await http.post(`/batches/${record.value.batchId}/lab-samples`, {
      sampleCode: code,
      sampleType: "Final",
      materialLotId: record.value.materials?.find((m) => m.role === "Produced")?.materialLotId
        ?? record.value.materials?.[0]?.materialLotId
        ?? null
    });
    ElMessage.success("已登记样品");
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  }
}

async function disposeSample(sampleId: string, disposition: LabSampleDisposition) {
  if (!record.value) return;
  try {
    const { value: comment } = await ElMessageBox.prompt(
      disposition === "Fail" ? "不合格必须填写对照规格的意见。" : "请填写判定意见（可空）。",
      disposition === "Fail" ? "样品不合格 · 意见" : "样品合格 · 意见"
    );
    const password = await esignPassword("样品判定");
    await http.post(`/batches/${record.value.batchId}/lab-samples/${sampleId}/disposition`, {
      password,
      disposition,
      comment
    });
    ElMessage.success("样品已判定");
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  }
}

async function releaseLot() {
  if (!record.value) return;
  try {
    const { value: reason } = await ElMessageBox.prompt(
      "请对照归档质检与四步握手填写放行意见。超差时必须说明偏差放行理由。",
      "质量放行 · 放行意见");
    const password = await esignPassword("质量放行");
    await http.post(`/batches/${record.value.batchId}/release`, { password, reason });
    ElMessage.success("批次已质量放行");
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  }
}

async function rejectLot() {
  if (!record.value) return;
  try {
    const { value: reason } = await ElMessageBox.prompt(
      "请填写拒收意见（对照质检超差或握手异常）。",
      "质量拒收 · 拒收意见");
    const password = await esignPassword("质量拒收");
    await http.post(`/batches/${record.value.batchId}/reject-disposition`, { password, reason });
    ElMessage.success("批次已质量拒收");
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  }
}

const { loading, error } = useLoad();

async function load() {
  try {
    record.value = (await http.get<BatchRecordDto>(`/batches/${route.params.id}/record`)).data;
    equipmentIndex.value = Object.fromEntries(
      (await http.get<EquipmentDto[]>("/equipment")).data.map((e) => [e.id, e.code]));
    error.value = "";
  } catch (e) {
    // 原先取数失败会留下空白页且无任何提示（根节点 v-if="record"），这里显式报错。
    error.value = (e as Error).message || "电子批记录加载失败";
  } finally {
    loading.value = false;
  }
}

onMounted(load);
</script>

<style scoped>
.block { margin-bottom: var(--space-5); }
.block h3 { margin: 0 0 var(--space-2); font-size: 16px; }
.block h4 { margin: var(--space-3) 0 var(--space-2); font-size: 14px; }
.muted { color: var(--muted); font-size: 12px; margin: 0 0 var(--space-2); }
@media print {
  .no-print { display: none !important; }
  .ebr { color: #111; }
}
</style>
