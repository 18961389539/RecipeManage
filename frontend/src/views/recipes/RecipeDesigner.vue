<template>
  <!-- 原先根节点是 v-if="detail"：取数失败会留下整页空白且无任何提示。 -->
  <div class="page-state" v-if="error || (!detail && loading)">
    <el-alert
      v-if="error"
      type="error"
      show-icon
      :closable="false"
      :title="`配方详情加载失败：${error}`"
      description="请确认该配方是否存在，或返回主配方列表重试。"
    />
    <el-skeleton v-else :rows="8" animated />
  </div>
  <div v-if="detail">
    <div class="page-title">
      <div>
        <h2>{{ detail.code }} {{ detail.name }}</h2>
        <span>产品 {{ detail.productName }} · {{ working ? statusLabel(working.status) : "无草稿" }} v{{ working?.versionNumber }}</span>
      </div>
      <div>
        <el-button @click="$router.push('/recipes')">返回配方列表</el-button>
        <el-button v-if="canAuthor" @click="openHeader">编辑抬头</el-button>
        <el-button v-if="canAuthor && working?.status === 'Draft'" @click="autoLayout">ISA-88 泳道排布</el-button>
        <el-button v-if="canAuthor && working?.status === 'Draft'" type="primary" :loading="saving || submitting" @click="save">保存工艺</el-button>
        <el-button v-if="canAuthor && working?.status === 'Draft'" :loading="submitting" @click="submit">提交审核</el-button>
        <el-button v-if="canDecide" type="success" @click="decide('Approved')">通过</el-button>
        <el-button v-if="canDecide" type="danger" @click="decide('Rejected')">驳回</el-button>
        <el-button v-if="canAuthor && working?.status === 'Rejected'" @click="reopen">重新打开</el-button>
        <el-button v-if="canAuthor && detail.approved && !detail.draft" @click="newVersion">升版</el-button>
      </div>
    </div>
    <el-dialog v-model="parallelVisible" title="新增并行单元规程" width="480px">
      <p class="muted">新单元与现有工步无前驱连线，可绑定不同 PLC 并行四步握手。编排完后用下方「添加连线」汇合到质检工步。</p>
      <el-form label-width="108px">
        <el-form-item label="单元规程"><el-input v-model="parallelForm.unit" /></el-form-item>
        <el-form-item label="首工步类型">
          <el-select v-model="parallelForm.type" style="width:100%">
            <el-option v-for="t in types" :key="t" :label="t" :value="t" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="parallelVisible = false">取消</el-button>
        <el-button type="primary" @click="confirmParallel">添加并行工步</el-button>
      </template>
    </el-dialog>
    <el-dialog v-model="headerVisible" title="主配方抬头" width="480px">
      <el-form :model="headerForm" label-width="96px">
        <el-form-item label="名称"><el-input v-model="headerForm.name" /></el-form-item>
        <el-form-item label="产品编码"><el-input v-model="headerForm.productCode" /></el-form-item>
        <el-form-item label="产品名称"><el-input v-model="headerForm.productName" /></el-form-item>
        <el-form-item label="说明"><el-input v-model="headerForm.description" type="textarea" :rows="2" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="headerVisible = false">取消</el-button>
        <el-button type="primary" :loading="savingHeader" @click="saveHeader">保存抬头</el-button>
      </template>
    </el-dialog>
    <div class="versions">
      <button
        v-for="v in detail.versions"
        :key="v.id"
        class="ver"
        :class="{ on: working?.id === v.id }"
        type="button"
        @click="viewingId = v.id"
      >
        v{{ v.versionNumber }} {{ statusLabel(v.status) }}
      </button>
      <el-button v-if="detail.versions.length > 1" size="small" type="primary" plain @click.stop="runCompare">版本对比</el-button>
    </div>
    <el-alert class="gap-after"
      v-if="working && !editable"
      :closable="false"
      type="warning"
      show-icon
      :title="`只读浏览 v${working.versionNumber}（${statusLabel(working.status)}）。草稿才可改工步与参数。`"
     
    />
    <p v-if="working?.changeNote" class="muted gap-after">变更说明：{{ working.changeNote }}</p>
    <el-dialog v-model="diffVisible" title="版本差异" width="720px">
      <div v-if="diff">
        <p>v{{ diff.fromVersion }} → v{{ diff.toVersion }}</p>
        <p v-if="diff.addedSteps.length">新增工步：{{ diff.addedSteps.join("、") }}</p>
        <p v-if="diff.removedSteps.length">删除工步：{{ diff.removedSteps.join("、") }}</p>
        <el-empty v-if="!diff.changes.length && !diff.addedSteps.length && !diff.removedSteps.length" description="两个版本工艺内容相同" />
        <el-table v-else :data="diff.changes" size="small" max-height="360">
          <el-table-column prop="path" label="路径" min-width="180" fixed />
          <el-table-column prop="before" label="之前" />
          <el-table-column prop="after" label="之后" />
        </el-table>
      </div>
    </el-dialog>
    <el-row :gutter="12">
      <el-col :span="5" :xs="24">
        <el-card header="工步列表">
          <div
            v-for="s in working?.steps ?? []"
            :key="s.id"
            class="step-item"
            :class="{ on: selectedId === s.id }"
            role="button"
            tabindex="0"
            @click="selectedId = s.id"
            @keydown.enter="selectedId = s.id"
          >
            <b>{{ s.code }}</b> {{ s.name }}
            <div class="muted">{{ s.unitProcedure || "UP" }} · {{ s.operation || s.type }} · Params[{{ s.parameters.length }}]</div>
          </div>
        </el-card>
      </el-col>
      <el-col :span="11" :xs="24">
        <el-card header="工艺工步编排（ISA-88 单元规程泳道）">
          <div class="palette">
            <el-button v-for="t in types" :key="t" size="small" :disabled="!editable" @click="addStep(t)">+ {{ t }}</el-button>
            <el-dropdown trigger="click" :disabled="!editable" @command="addFromTemplate">
              <el-button size="small" type="success" plain :disabled="!editable">从相模板添加</el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <template v-for="cls in phaseClasses" :key="cls.id">
                    <el-dropdown-item disabled>{{ cls.code }} · {{ cls.name }}</el-dropdown-item>
                    <el-dropdown-item
                      v-for="t in cls.templates"
                      :key="t.id"
                      :command="t.id"
                    >
                      {{ t.code }} {{ t.name }}
                    </el-dropdown-item>
                  </template>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
            <el-button size="small" type="primary" plain :disabled="!editable" @click="openParallel">+ 并行单元规程</el-button>
            <el-button size="small" type="danger" :disabled="!editable || !selected" @click="removeStep">删除工步</el-button>
          </div>
          <div class="lanes">
            <span v-for="lane in laneNames" :key="lane" class="lane-chip">{{ lane }}</span>
          </div>
          <div style="height: 480px">
            <VueFlow
              :nodes="nodes"
              :edges="edges"
              :node-types="nodeTypes"
              :nodes-draggable="editable"
              :nodes-connectable="editable"
              fit-view-on-init
              @nodes-change="onNodes"
              @connect="onConnect"
              @node-click="onNodeClick"
            >
              <Background />
              <Controls />
            </VueFlow>
          </div>
          <div v-if="editable" class="edge-editor">
            <div class="muted">工艺连线：同一单元内串行；并行单元不要互连。跨单元只从末工步进入下一单元首工步（汇合质检）。</div>
            <div v-for="(row, i) in edgeRows" :key="`${row.fromId}-${row.toId}-${i}`" class="edge-row">
              <span>{{ row.fromCode }} → {{ row.toCode }}</span>
              <el-button size="small" text type="danger" @click="removeEdgeAt(i)">删除连线</el-button>
            </div>
            <div class="edge-add">
              <el-select v-model="edgeFromId" placeholder="前驱工步" size="small" style="width:140px">
                <el-option v-for="s in working?.steps ?? []" :key="s.id" :label="`${s.code} ${s.unitProcedure || ''}`" :value="s.id" />
              </el-select>
              <span>→</span>
              <el-select v-model="edgeToId" placeholder="后继 / 汇合" size="small" style="width:140px">
                <el-option v-for="s in working?.steps ?? []" :key="s.id" :label="`${s.code} ${s.unitProcedure || ''}`" :value="s.id" />
              </el-select>
              <el-button size="small" :disabled="!edgeFromId || !edgeToId" @click="addEdgeFromSelect">添加连线</el-button>
              <el-button size="small" type="primary" plain :disabled="!selected" @click="joinToSelected">汇合到当前工步</el-button>
            </div>
          </div>
        </el-card>
      </el-col>
      <el-col :span="8" :xs="24">
        <el-card header="参数矩阵 / Setpoints">
          <div v-if="selected">
            <el-form label-width="96px" size="small">
              <el-form-item label="编码"><el-input v-model="selected.code" :disabled="!editable" /></el-form-item>
              <el-form-item label="名称"><el-input v-model="selected.name" :disabled="!editable" /></el-form-item>
              <el-form-item label="类型"><el-input :model-value="selected.type" disabled /></el-form-item>
              <el-form-item>
                <template #label><HelpTip term="Unit Procedure">单元规程</HelpTip></template>
                <el-input v-model="selected.unitProcedure" :disabled="!editable" placeholder="ISA-88 Unit Procedure" />
              </el-form-item>
              <el-form-item>
                <template #label><HelpTip term="Operation">操作</HelpTip></template>
                <el-input v-model="selected.operation" :disabled="!editable" placeholder="ISA-88 Operation" />
              </el-form-item>
              <el-form-item label="说明"><el-input v-model="selected.description" :disabled="!editable" /></el-form-item>
              <el-form-item label="看门狗(s)"><el-input-number v-model="selected.watchdogSeconds" :min="5" :disabled="!editable" /></el-form-item>
            </el-form>
            <el-table :data="selected.parameters" size="small" max-height="280">
              <el-table-column prop="slotIndex" label="槽" width="48" fixed />
              <el-table-column label="参数" min-width="90">
                <template #default="{ row }"><el-input v-model="row.name" :disabled="!editable" /></template>
              </el-table-column>
              <el-table-column label="设定值" width="88">
                <template #default="{ row }"><el-input-number v-model="row.setpoint" :controls="false" :disabled="!editable" /></template>
              </el-table-column>
              <el-table-column label="下限" width="72">
                <template #default="{ row }"><el-input-number v-model="row.min" :controls="false" :disabled="!editable" /></template>
              </el-table-column>
              <el-table-column label="上限" width="72">
                <template #default="{ row }"><el-input-number v-model="row.max" :controls="false" :disabled="!editable" /></template>
              </el-table-column>
              <el-table-column label="单位" width="64">
                <template #default="{ row }"><el-input v-model="row.engineeringUnit" :disabled="!editable" /></template>
              </el-table-column>
              <el-table-column label="写PLC" width="64">
                <template #default="{ row }"><el-switch v-model="row.writeToPlc" :disabled="!editable" /></template>
              </el-table-column>
              <el-table-column label="质检" width="56">
                <template #default="{ row }"><el-switch v-model="row.archiveAsQuality" :disabled="!editable" /></template>
              </el-table-column>
              <el-table-column label="随批缩放" width="80">
                <template #default="{ row }"><el-switch v-model="row.scaleWithBatch" :disabled="!editable" /></template>
              </el-table-column>
            </el-table>
            <el-button class="gap-before-sm" size="small" :disabled="!editable" @click="addParam">新增参数槽</el-button>
            <el-button class="gap-before-sm" size="small" :disabled="!editable" @click="removeLastParam">删除末槽</el-button>
          </div>
          <el-empty v-else description="选择左侧工步或点击画布节点" />
          <el-divider />
          <div class="muted" v-for="a in working?.approvals ?? []" :key="a.id">
            <b>{{ levelLabel(a.level) }}</b> · {{ a.decision }} · {{ a.reviewerName ?? "待审" }}
            <div>{{ a.meaning }}</div>
            <div v-if="a.comment">意见：{{ a.comment }}</div>
          </div>
        </el-card>
      </el-col>
    </el-row>
    <el-card class="gap-before" header="控制参数矩阵 / Setpoints（工步 × 设定值）">
      <SetpointMatrix
        :steps="working?.steps ?? []"
        :selected-id="selectedId"
        :readonly="!editable"
        @select="selectedId = $event"
      />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed, markRaw, onMounted, reactive, ref, watch } from "vue";
import { useRoute } from "vue-router";
import { VueFlow, type Connection, type Edge, type Node, type NodeChange, type NodeComponent, type NodeMouseEvent } from "@vue-flow/core";
import { Background } from "@vue-flow/background";
import { Controls } from "@vue-flow/controls";
import "@vue-flow/core/dist/style.css";
import "@vue-flow/core/dist/theme-default.css";
import "@vue-flow/controls/dist/style.css";
import { ElMessage, ElMessageBox } from "element-plus";
import http from "../../api/http";
import { esignPassword } from "../../utils/esign";
import type {
  EquipmentClassDto,
  ParameterDto,
  RecipeDetailDto,
  RecipeVersionDiffDto,
  RecipeVersionDto,
  StepType
} from "../../api/types";
import { recipeStatusLabel as statusLabel } from "../../utils/labels";
import { useLoad } from "../../utils/useLoad";
import HelpTip from "../../components/HelpTip.vue";

const { loading, error } = useLoad();
const savingHeader = ref(false);
const saving = ref(false);
const submitting = ref(false);
import { useAuthStore } from "../../stores/auth";
import StepFlowNode from "../../components/StepFlowNode.vue";
import SetpointMatrix from "../../components/SetpointMatrix.vue";
import { layoutByLaneOrdinal } from "../../procedureLayout";

const route = useRoute();
const auth = useAuthStore();
const detail = ref<RecipeDetailDto | null>(null);
const selectedId = ref<string | null>(null);
const viewingId = ref<string | null>(null);
const diff = ref<RecipeVersionDiffDto | null>(null);
const diffVisible = ref(false);
const headerVisible = ref(false);
const headerForm = reactive({ name: "", productCode: "", productName: "", description: "" });
const parallelVisible = ref(false);
const parallelForm = reactive({ unit: "", type: "Cool" as StepType });
const edgeFromId = ref("");
const edgeToId = ref("");
const nodeTypes = { recipeStep: markRaw(StepFlowNode) as unknown as NodeComponent };
const working = computed<RecipeVersionDto | null>(() => {
  const versions = detail.value?.versions ?? [];
  return versions.find((v) => v.id === viewingId.value)
    ?? detail.value?.draft
    ?? detail.value?.approved
    ?? null;
});
const selected = computed(() => working.value?.steps.find((s) => s.id === selectedId.value) ?? null);
const canAuthor = computed(() => {
  const role = auth.user?.role;
  return role === "Admin" || role === "ProcessEngineer";
});
const editable = computed(() => canAuthor.value && working.value?.status === "Draft");
const types: StepType[] = ["Heat", "Hold", "Cool", "Mix", "Pressure", "QualityCheck", "Wait", "Transfer", "ManualConfirm"];
const phaseClasses = ref<EquipmentClassDto[]>([]);
const laneNames = computed(() => {
  const names: string[] = [];
  for (const s of [...(working.value?.steps ?? [])].sort((a, b) => a.ordinal - b.ordinal)) {
    const lane = s.unitProcedure?.trim() || "UP-01 热处理单元";
    if (!names.includes(lane)) names.push(lane);
  }
  return names;
});

const nodes = computed<Node[]>(() =>
  (working.value?.steps ?? []).map((s) => ({
    id: s.id,
    type: "recipeStep",
    position: { x: s.canvasX, y: s.canvasY },
    data: { label: `${s.code} ${s.name}`, code: s.code, name: s.name, type: s.type, operation: s.operation, unitProcedure: s.unitProcedure },
    selected: s.id === selectedId.value
  }))
);
const edges = computed<Edge[]>(() =>
  (working.value?.edges ?? []).map((e, i) => ({
    id: e.id ?? `e${i}`,
    source: e.fromStepId,
    target: e.toStepId
  }))
);
const edgeRows = computed(() =>
  (working.value?.edges ?? []).map((e) => {
    const from = working.value?.steps.find((s) => s.id === e.fromStepId);
    const to = working.value?.steps.find((s) => s.id === e.toStepId);
    return {
      fromId: e.fromStepId,
      toId: e.toStepId,
      fromCode: from?.code ?? e.fromStepId.slice(0, 8),
      toCode: to?.code ?? e.toStepId.slice(0, 8)
    };
  })
);

const canDecide = computed(() => {
  const pending = working.value?.approvals.find((a) => a.decision === "Pending");
  if (!pending || working.value?.status !== "InReview") return false;
  const role = auth.user?.role;
  return role === "Admin" || (pending.level === "Supervisor" && role === "Supervisor") || (pending.level === "Quality" && role === "Quality");
});

async function load() {
  try {
    detail.value = (await http.get<RecipeDetailDto>(`/recipes/${route.params.id}`)).data;
    const versions = detail.value.versions ?? [];
    if (!viewingId.value || !versions.some((v) => v.id === viewingId.value))
      viewingId.value = detail.value.draft?.id ?? detail.value.approved?.id ?? versions[0]?.id ?? null;
    error.value = "";
  } catch (e) {
    // 原先取数失败会留下空白页且无任何提示（根节点 v-if="detail"），这里显式报错。
    error.value = (e as Error).message || "配方详情加载失败";
  } finally {
    loading.value = false;
  }
}

function openHeader() {
  if (!detail.value) return;
  headerForm.name = detail.value.name;
  headerForm.productCode = detail.value.productCode;
  headerForm.productName = detail.value.productName;
  headerForm.description = detail.value.description ?? "";
  headerVisible.value = true;
}

async function saveHeader() {
  if (!detail.value || savingHeader.value) return;
  savingHeader.value = true;
  try {
    await http.put(`/recipes/${detail.value.id}`, {
      name: headerForm.name,
      productCode: headerForm.productCode,
      productName: headerForm.productName,
      description: headerForm.description || null
    });
    ElMessage.success("已更新主配方抬头");
    headerVisible.value = false;
    await load();
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    savingHeader.value = false;
  }
}

function defaultOperation(type: StepType) {
  const map: Record<StepType, string> = {
    Heat: "OP-Heat 升温",
    Hold: "OP-Hold 保温",
    Cool: "OP-Cool 冷却",
    Mix: "OP-Mix 搅拌",
    Pressure: "OP-Press 加压",
    Transfer: "OP-Xfer 转移",
    QualityCheck: "OP-QC 质检",
    ManualConfirm: "OP-Manual 人工确认",
    Wait: "OP-Wait 等待"
  };
  return map[type];
}

watch(working, (v) => {
  if (v?.steps.length && !v.steps.some((s) => s.id === selectedId.value))
    selectedId.value = v.steps[0].id;
}, { immediate: true });

function defaultParameters(type: StepType): ParameterDto[] {
  if (type === "ManualConfirm")
    return [{ slotIndex: 0, name: "确认意见", engineeringUnit: "", setpoint: 0, writeToPlc: false, archiveAsQuality: false, scaleWithBatch: false }];
  if (type === "QualityCheck")
    return [{ slotIndex: 0, name: "硬度", engineeringUnit: "HB", setpoint: 95, min: 90, max: 110, writeToPlc: false, archiveAsQuality: true, scaleWithBatch: false }];
  if (type === "Wait")
    return [{ slotIndex: 0, name: "等待时长", engineeringUnit: "s", setpoint: 5, min: 0.5, max: 3600, writeToPlc: false, archiveAsQuality: false, scaleWithBatch: false }];
  if (type === "Heat")
    return [
      { slotIndex: 0, name: "目标温度", engineeringUnit: "℃", setpoint: 530, min: 520, max: 540, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: false },
      { slotIndex: 1, name: "升温斜率", engineeringUnit: "℃/min", setpoint: 8, min: 4, max: 12, writeToPlc: true, archiveAsQuality: false, scaleWithBatch: false },
      { slotIndex: 2, name: "升温时长", engineeringUnit: "s", setpoint: 8, min: 0.5, max: 3600, writeToPlc: false, archiveAsQuality: false, scaleWithBatch: false }
    ];
  if (type === "Hold")
    return [
      { slotIndex: 0, name: "保温温度", engineeringUnit: "℃", setpoint: 530, min: 525, max: 535, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: false },
      { slotIndex: 1, name: "保温时长", engineeringUnit: "s", setpoint: 8, min: 1, max: 3600, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: false }
    ];
  if (type === "Cool")
    return [
      { slotIndex: 0, name: "终点温度", engineeringUnit: "℃", setpoint: 40, min: 20, max: 60, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: false },
      { slotIndex: 1, name: "冷却时长", engineeringUnit: "s", setpoint: 6, min: 1, max: 600, writeToPlc: true, archiveAsQuality: false, scaleWithBatch: false }
    ];
  if (type === "Mix")
    return [
      { slotIndex: 0, name: "搅拌转速", engineeringUnit: "rpm", setpoint: 60, min: 10, max: 200, writeToPlc: true, archiveAsQuality: false, scaleWithBatch: false },
      { slotIndex: 1, name: "搅拌时长", engineeringUnit: "s", setpoint: 8, min: 1, max: 600, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: false }
    ];
  if (type === "Pressure")
    return [
      { slotIndex: 0, name: "目标压力", engineeringUnit: "bar", setpoint: 2.5, min: 1, max: 6, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: false },
      { slotIndex: 1, name: "保压时长", engineeringUnit: "s", setpoint: 6, min: 1, max: 600, writeToPlc: true, archiveAsQuality: false, scaleWithBatch: false }
    ];
  if (type === "Transfer")
    return [
      { slotIndex: 0, name: "转移量", engineeringUnit: "kg", setpoint: 50, min: 1, max: 500, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: true },
      { slotIndex: 1, name: "转移时长", engineeringUnit: "s", setpoint: 5, min: 1, max: 300, writeToPlc: true, archiveAsQuality: false, scaleWithBatch: false }
    ];
  return [{ slotIndex: 0, name: "设定值", engineeringUnit: "", setpoint: 0, writeToPlc: true, archiveAsQuality: false, scaleWithBatch: false }];
}

function addFromTemplate(templateId: string) {
  const template = phaseClasses.value.flatMap((c) => c.templates).find((t) => t.id === templateId);
  if (!template) return;
  addStep(template.stepType, {
    name: template.name,
    operation: template.operation,
    watchdogSeconds: template.watchdogSeconds,
    parameters: template.parameters.map((p) => ({
      slotIndex: p.slotIndex,
      name: p.name,
      engineeringUnit: p.engineeringUnit,
      setpoint: p.setpoint,
      min: p.min,
      max: p.max,
      writeToPlc: p.writeToPlc,
      archiveAsQuality: p.archiveAsQuality,
      scaleWithBatch: p.scaleWithBatch ?? false
    }))
  });
}

function addStep(type: StepType, opts?: {
  unitProcedure?: string;
  chain?: boolean;
  name?: string;
  operation?: string;
  watchdogSeconds?: number;
  parameters?: ParameterDto[];
}) {
  if (!working.value || working.value.status !== "Draft") return;
  const n = working.value.steps.length;
  const last = working.value.steps[n - 1];
  const id = crypto.randomUUID();
  const unit = opts?.unitProcedure?.trim() || last?.unitProcedure || "UP-01 热处理单元";
  const chain = opts?.chain !== false;
  const laneIndex = Math.max(0, laneNames.value.indexOf(unit) === -1 ? laneNames.value.length : laneNames.value.indexOf(unit));
  const inLane = working.value.steps.filter((s) => (s.unitProcedure || "UP-01 热处理单元") === unit).length;
  working.value.steps.push({
    id,
    code: `S${(n + 1) * 10}`,
    name: opts?.name?.trim() || type,
    type,
    ordinal: n,
    canvasX: 80 + inLane * 210,
    canvasY: 48 + laneIndex * 170,
    watchdogSeconds: opts?.watchdogSeconds && opts.watchdogSeconds > 0 ? opts.watchdogSeconds : 120,
    description: "",
    unitProcedure: unit,
    operation: opts?.operation?.trim() || defaultOperation(type),
    parameters: opts?.parameters ?? defaultParameters(type)
  });
  if (n > 0 && chain && last)
    working.value.edges.push({ fromStepId: last.id, toStepId: id });
  selectedId.value = id;
}

function nextParallelUnitName() {
  const n = laneNames.value.length + 1;
  return `UP-${String(n).padStart(2, "0")} 并行单元`;
}

function openParallel() {
  parallelForm.unit = nextParallelUnitName();
  parallelForm.type = "Cool";
  parallelVisible.value = true;
}

function confirmParallel() {
  const unit = parallelForm.unit.trim();
  if (!unit) {
    ElMessage.warning("必须填写单元规程名称");
    return;
  }
  addStep(parallelForm.type, { unitProcedure: unit, chain: false });
  parallelVisible.value = false;
}

function addEdgeFromSelect() {
  if (!working.value || !editable.value) return;
  const from = edgeFromId.value;
  const to = edgeToId.value;
  if (!from || !to || from === to) {
    ElMessage.warning("请选择不同的前驱与后继工步");
    return;
  }
  if (working.value.edges.some((e) => e.fromStepId === from && e.toStepId === to)) {
    ElMessage.info("该连线已存在");
    return;
  }
  working.value.edges.push({ fromStepId: from, toStepId: to });
}

function joinToSelected() {
  if (!working.value || !editable.value || !selectedId.value) return;
  const to = selectedId.value;
  const outgoing = new Set(working.value.edges.map((e) => e.fromStepId));
  const terminals = working.value.steps.filter((s) => s.id !== to && !outgoing.has(s.id));
  if (!terminals.length) {
    ElMessage.info("没有可汇合的末工步（所有工步已有后继）");
    return;
  }
  for (const step of terminals) {
    if (working.value.edges.some((e) => e.fromStepId === step.id && e.toStepId === to))
      continue;
    working.value.edges.push({ fromStepId: step.id, toStepId: to });
  }
  ElMessage.success(`已将 ${terminals.map((s) => s.code).join("、")} 汇合到当前工步`);
}

function removeEdgeAt(index: number) {
  if (!working.value || !editable.value) return;
  working.value.edges.splice(index, 1);
}

function removeStep() {
  if (!working.value || !editable.value || !selectedId.value) return;
  const id = selectedId.value;
  working.value.steps = working.value.steps.filter((s) => s.id !== id);
  working.value.edges = working.value.edges.filter((e) => e.fromStepId !== id && e.toStepId !== id);
  selectedId.value = working.value.steps[0]?.id ?? null;
}

function removeLastParam() {
  if (!selected.value || !editable.value || selected.value.parameters.length <= 1) return;
  selected.value.parameters.pop();
}

async function runCompare() {
  if (!detail.value || detail.value.versions.length < 2) return;
  const versions = [...detail.value.versions].sort((a, b) => a.versionNumber - b.versionNumber);
  const to = working.value?.versionNumber ?? versions[versions.length - 1].versionNumber;
  const idx = versions.findIndex((v) => v.versionNumber === to);
  const from = (idx > 0 ? versions[idx - 1] : versions[0]).versionNumber;
  if (from === to) {
    ElMessage.info("请选择与上一版本不同的版本再对比");
    return;
  }
  try {
    diff.value = (await http.get<RecipeVersionDiffDto>(`/recipes/${detail.value.id}/compare`, {
      params: { fromVersion: from, toVersion: to }
    })).data;
    diffVisible.value = true;
  } catch (e) {
    ElMessage.error((e as Error).message);
  }
}

function autoLayout() {
  if (!working.value) return;
  const pos = layoutByLaneOrdinal(working.value.steps.map((s) => ({
    id: s.id,
    unitProcedure: s.unitProcedure,
    ordinal: s.ordinal
  })));
  for (const s of working.value.steps) {
    const p = pos[s.id];
    if (!p) continue;
    s.canvasX = p.x;
    s.canvasY = p.y;
  }
}

function addParam() {
  if (!selected.value || selected.value.parameters.length >= 16) return;
  selected.value.parameters.push({
    slotIndex: selected.value.parameters.length,
    name: "Param",
    engineeringUnit: "",
    setpoint: 0,
    min: null,
    max: null,
    writeToPlc: true,
    archiveAsQuality: false,
    scaleWithBatch: false
  });
}

function onNodes(changes: NodeChange[]) {
  if (!working.value || !editable.value) return;
  for (const change of changes) {
    if (change.type === "position" && change.position) {
      const step = working.value.steps.find((s) => s.id === change.id);
      if (step) {
        step.canvasX = change.position.x;
        step.canvasY = change.position.y;
      }
    }
    if (change.type === "select" && change.selected)
      selectedId.value = change.id;
  }
}

function onConnect(c: Connection) {
  if (!working.value || working.value.status !== "Draft" || !c.source || !c.target) return;
  if (c.source === c.target) return;
  if (working.value.edges.some((e) => e.fromStepId === c.source && e.toStepId === c.target))
    return;
  working.value.edges.push({ fromStepId: c.source, toStepId: c.target });
}

function onNodeClick({ node }: NodeMouseEvent) {
  selectedId.value = node.id;
}

async function save() {
  if (!detail.value || !working.value || saving.value) return false;
  saving.value = true;
  try {
    const { value: changeReason } = await ElMessageBox.prompt(
      "GxP：保存工艺会改写草稿 Procedure / Steps / Parameters，必须填写变更原因并电子签名。",
      "保存工艺 · 变更控制",
      { inputPlaceholder: "变更原因（必填）", inputValidator: (v: string) => (v?.trim() ? true : "必须填写变更原因") }
    );
    const password = await esignPassword(
      "保存工艺",
      "签署含义：我作为工艺工程师确认本次 Procedure / Steps / Parameters 变更准确，并记录变更原因。"
    );
    await http.put(`/recipes/${detail.value.id}/procedure`, {
      steps: working.value.steps,
      edges: working.value.edges.map((e) => ({ fromStepId: e.fromStepId, toStepId: e.toStepId })),
      password,
      changeReason
    });
    ElMessage.success("已电子签名保存工艺");
    await load();
    return true;
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
    return false;
  } finally {
    saving.value = false;
  }
}

async function submit() {
  if (!detail.value || !working.value || submitting.value) return;
  submitting.value = true;
  try {
    const saved = await save();
    if (!saved) return;
    const password = await esignPassword(
      "提交审核",
      "签署含义：我作为工艺工程师确认本版本 Procedure / Steps 与 Parameters / Setpoints 准确，提交多级审核。"
    );
    await http.post(`/recipes/${detail.value.id}/submit`, { password });
    ElMessage.success("已电子签名并提交多级审核（工艺主管 → 质量）");
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    submitting.value = false;
  }
}

async function decide(decision: "Approved" | "Rejected") {
  try {
    const { value: comment } = await ElMessageBox.prompt(
      `${pendingMeaning.value}\n\n${decision === "Approved" ? "审核意见（通过）" : "驳回原因"}`,
      pendingLevelLabel.value
    );
    const password = await esignPassword(pendingLevelLabel.value);
    await http.post(`/recipes/${detail.value!.id}/decide`, { decision, comment, password });
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  }
}

const pendingLevelLabel = computed(() => {
  const pending = working.value?.approvals.find((a) => a.decision === "Pending");
  return pending ? `当前节点：${levelLabel(pending.level)}` : "审核";
});
const pendingMeaning = computed(() =>
  working.value?.approvals.find((a) => a.decision === "Pending")?.meaning
  ?? "请再次输入登录密码作为电子签名。"
);

function levelLabel(level: string) {
  return ({ Author: "工艺工程师提交", Supervisor: "工艺主管", Quality: "质量" } as Record<string, string>)[level] ?? level;
}

async function reopen() {
  if (!detail.value) return;
  try {
    const password = await esignPassword(
      "重新打开驳回版本",
      "签署含义：我作为工艺工程师确认将被驳回版本重新打开为草稿，并继续修订 Procedure / Setpoints。"
    );
    await http.post(`/recipes/${detail.value.id}/reopen`, { password });
    await load();
    ElMessage.success("已重新打开为草稿，可改工艺后再次提交审核。");
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  }
}

async function newVersion() {
  try {
    const { value } = await ElMessageBox.prompt("变更说明", "创建新版本");
    const password = await esignPassword("升版");
    await http.post(`/recipes/${detail.value!.id}/new-version`, { changeNote: value, password });
    await load();
    viewingId.value = detail.value?.draft?.id ?? viewingId.value;
    ElMessage.success(`已创建草稿 v${detail.value?.draft?.versionNumber ?? ""}，在此版本改工艺后再提交审核。`);
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  }
}

onMounted(async () => {
  await load();
  phaseClasses.value = (await http.get<EquipmentClassDto[]>("/equipment/classes")).data;
});
</script>

<style scoped>
.palette { display: flex; gap: var(--space-2); flex-wrap: wrap; margin-bottom: var(--space-2); }
.step-item { padding: var(--space-2); border-radius: 8px; cursor: pointer; border: 1px solid transparent; margin-bottom: var(--space-2); }
.step-item.on { background: var(--tint); border-color: var(--accent); }
.muted { color: var(--muted); font-size: 12px; }
.versions { display: flex; gap: var(--space-2); flex-wrap: wrap; margin: 0 0 var(--space-3); }
.ver {
  border: 1px solid var(--line); background: var(--panel); color: var(--text-body);
  border-radius: 999px; padding: var(--space-1) var(--space-3); cursor: pointer;
}
.ver.on { border-color: var(--accent); background: var(--tint); color: var(--text); }
.lanes { display: flex; gap: var(--space-2); flex-wrap: wrap; margin-bottom: var(--space-2); }
.lane-chip {
  font-size: 11px; color: var(--muted); border: 1px dashed var(--accent);
  border-radius: 999px; padding: 2px var(--space-3);
}
.edge-editor { margin-top: var(--space-3); }
.edge-row { display: flex; align-items: center; justify-content: space-between; font-size: 12px; color: var(--text-body); }
.edge-add { display: flex; align-items: center; gap: var(--space-2); margin-top: var(--space-2); flex-wrap: wrap; }
</style>
