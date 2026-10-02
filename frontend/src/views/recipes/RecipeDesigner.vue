<template>
  <!-- 原先根节点是 v-if="detail"：取数失败会留下整页空白且无任何提示。 -->
  <div class="page-state" v-if="error || (!detail && loading)">
    <el-alert
      v-if="error"
      type="error"
      show-icon
      :closable="false"
      :title="`配方详情加载失败：${error}`"
      :description="$t('请确认该配方是否存在，或返回主配方列表重试。')"
    />
    <el-skeleton v-else :rows="8" animated />
  </div>
  <div v-if="detail">
    <div class="page-title sticky-actions">
      <div>
        <h2>{{ detail.code }} {{ detail.name }}<span v-if="dirty" class="unsaved"> {{ $t("· 未保存") }}</span></h2>
        <span>{{ $t("产品 {0} · {1} v{2}", [detail.productName, working ? statusLabel(working.status) : $t("无草稿"), working?.versionNumber ?? "—"]) }}</span>
        <!-- 走哪条链是"这份配方要谁签"的声明，和提交动作同属工艺工程师，所以放在这里而不是配置台。 -->
        <HelpTip v-if="canAuthor && working?.status === 'Draft'" term="审批链" plain placement="bottom">
          <el-select
            v-model="chainCode"
            class="chain-pick"
            size="small"
            :loading="chainSaving"
            @change="pickChain"
          >
            <el-option :value="DEFAULT_CHAIN" :label="defaultChainLabel" />
            <el-option v-for="c in chains" :key="c.code" :value="c.code" :label="`${c.name} · ${chainPath(c)}`" />
          </el-select>
        </HelpTip>
      </div>
      <div>
        <el-button @click="$router.push('/recipes')">{{ $t("返回配方列表") }}</el-button>
        <el-button v-if="canAuthor" @click="headerVisible = true">{{ $t("编辑抬头") }}</el-button>
        <el-button v-if="canAuthor && working?.status === 'Draft'" @click="autoLayout">{{ $t("ISA-88 泳道排布") }}</el-button>
        <HelpTip v-if="canAuthor && working?.status === 'Draft'" term="保存工艺" chord="ctrl+s" allow-in-input plain placement="bottom">
          <el-button type="primary" :loading="saving || submitting" @click="save">{{ $t("保存工艺") }}</el-button>
        </HelpTip>
        <HelpTip v-if="canAuthor && working?.status === 'Draft'" term="提交审核" chord="ctrl+enter" allow-in-input plain placement="bottom">
          <el-button :loading="submitting" @click="submit">{{ $t("提交审核") }}</el-button>
        </HelpTip>
        <HelpTip v-if="canDecide" term="通过" chord="ctrl+enter" allow-in-input plain placement="bottom">
          <el-button type="success" :loading="busyAction === 'decide'" :disabled="!!busyAction" @click="decide('Approved')">{{ $t("通过") }}</el-button>
        </HelpTip>
        <HelpTip v-if="canDecide" term="驳回" plain placement="bottom">
          <el-button type="danger" :loading="busyAction === 'reject'" :disabled="!!busyAction" @click="decide('Rejected')">{{ $t("驳回") }}</el-button>
        </HelpTip>
        <el-button v-if="canAuthor && working?.status === 'Rejected'" :loading="busyAction === 'reopen'" :disabled="!!busyAction" @click="reopen">{{ $t("重新打开") }}</el-button>
        <el-button v-if="canAuthor && detail.approved && !detail.draft" :loading="busyAction === 'version'" :disabled="!!busyAction" @click="newVersion">{{ $t("升版") }}</el-button>
      </div>
    </div>

    <div class="versions">
      <button
        v-for="v in detail.versions"
        :key="v.id"
        class="ver"
        :class="{ on: working?.id === v.id }"
        type="button"
        @click="viewVersion(v.id)"
      >
        v{{ v.versionNumber }} {{ statusLabel(v.status) }}
      </button>
      <el-button v-if="detail.versions.length > 1" size="small" type="primary" plain @click.stop="runCompare">{{ $t("版本对比") }}</el-button>
    </div>
    <el-alert class="gap-after"
      v-if="working && !editable"
      :closable="false"
      type="warning"
      show-icon
      :title="$t('只读浏览 v{0}（{1}）。草稿才可改工步与参数。', [working.versionNumber, statusLabel(working.status)])"
    />
    <p v-if="working?.changeNote" class="muted gap-after">{{ $t("变更说明：{0}", [working.changeNote]) }}</p>

    <el-row :gutter="12">
      <el-col :span="5" :xs="24">
        <el-card :header="$t('工步列表')">
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
            <div class="muted">{{ s.unitProcedure || "UP" }} · {{ phaseTypeLabel(s) }}{{ programHint(s) }} · {{ $t("参数 {0} 槽", [s.parameters.length]) }}</div>
          </div>
        </el-card>
      </el-col>
      <el-col :span="11" :xs="24">
        <el-card :header="$t('工艺工步编排（ISA-88 单元规程泳道）')">
          <div class="palette">
            <span class="palette-label"><HelpTip term="当前设备类">{{ $t("设备类") }}</HelpTip></span>
            <el-select
              v-model="paletteClassId"
              size="small"
              style="width: 220px"
              :disabled="!phaseClasses.length"
              :placeholder="$t('设备类')"
              @change="onPaletteClassChange"
            >
              <el-option
                v-for="cls in phaseClasses"
                :key="cls.id"
                :label="`${cls.code} · ${cls.name}`"
                :value="cls.id"
              />
            </el-select>
            <!-- HelpTip 必须包在 el-dropdown 外层：把 tooltip 塞进 dropdown 的触发槽里，
                 两个弹层组件会抢同一个触发元素，结果是下拉根本打不开。 -->
            <HelpTip
              term="从相模板添加"
              :extra="editable && paletteClass && !paletteTemplates.length ? `${paletteClass.code} 还没有相模板` : ''"
              plain
            >
              <el-dropdown trigger="click" :disabled="!editable || !paletteTemplates.length" @command="addFromTemplate">
                <el-button size="small" type="primary" :disabled="!editable || !paletteTemplates.length">{{ $t("从相模板添加") }}</el-button>
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item v-for="t in paletteTemplates" :key="t.id" :command="t.id">
                      {{ t.code }} {{ t.name }} · 程序 {{ templateProgram(t) }}
                    </el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
            </HelpTip>
            <el-button v-if="canAuthor || auth.can('Admin')" size="small" link type="primary" @click="openLibrary()">{{ $t("管理相库") }}</el-button>
            <span class="palette-label">{{ $t("上位机") }}</span>
            <el-button v-for="t in hostStepTypes" :key="t" size="small" :disabled="!editable" @click="addStep(t)">+ {{ stepTypeLabel(t) }}</el-button>
            <el-button size="small" type="primary" plain :disabled="!editable" @click="parallelVisible = true">{{ $t("+ 并行单元规程") }}</el-button>
            <el-button size="small" type="danger" :disabled="!editable || !selected" @click="removeStep">{{ $t("删除工步") }}</el-button>
          </div>
          <p v-if="editable && paletteClass && !paletteTemplates.length" class="muted">
            {{ $t("{0} 没有相模板。", [paletteClass.code]) }}
            <el-button link type="primary" @click="openLibrary()">{{ $t("去相库添加") }}</el-button>
          </p>
          <div class="lanes">
            <button
              v-for="lane in laneNames"
              :key="lane"
              class="lane-chip"
              type="button"
              :class="{ on: currentLane === lane }"
              @click="pickLane(lane)"
            >{{ lane }}<span v-if="laneClassCode(lane)"> · {{ laneClassCode(lane) }}</span></button>
          </div>
          <div class="flow-canvas" :style="{ height: `${flowHeight}px` }">
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
              <Background :gap="18" :size="1" />
              <Controls />
            </VueFlow>
          </div>
          <div v-if="editable" class="edge-editor">
            <div class="muted">{{ $t("工艺连线：同一单元内串行；并行单元不要互连。跨单元只从末工步进入下一单元首工步（汇合质检）。") }}</div>
            <div v-for="(row, i) in edgeRows" :key="`${row.fromId}-${row.toId}-${i}`" class="edge-row">
              <span>{{ row.fromCode }} → {{ row.toCode }}</span>
              <el-button size="small" text type="danger" @click="removeEdgeAt(i)">{{ $t("删除连线") }}</el-button>
            </div>
            <div class="edge-add">
              <el-select v-model="edgeFromId" :placeholder="$t('前驱工步')" size="small" style="width:140px">
                <el-option v-for="s in working?.steps ?? []" :key="s.id" :label="`${s.code} ${s.unitProcedure || ''}`" :value="s.id" />
              </el-select>
              <span>→</span>
              <el-select v-model="edgeToId" :placeholder="$t('后继 / 汇合')" size="small" style="width:140px">
                <el-option v-for="s in working?.steps ?? []" :key="s.id" :label="`${s.code} ${s.unitProcedure || ''}`" :value="s.id" />
              </el-select>
              <el-button size="small" :disabled="!edgeFromId || !edgeToId" @click="addEdgeFromSelect">{{ $t("添加连线") }}</el-button>
              <el-button size="small" type="primary" plain :disabled="!selected" @click="joinToSelected">{{ $t("汇合到当前工步") }}</el-button>
            </div>
          </div>
        </el-card>
      </el-col>
      <el-col :span="8" :xs="24">
        <el-card :header="$t('工步字段')">
          <div v-if="selected">
            <el-form label-width="96px" size="small">
              <el-form-item :label="$t('编码')"><el-input v-model="selected.code" :disabled="!editable" /></el-form-item>
              <el-form-item :label="$t('名称')"><el-input v-model="selected.name" :disabled="!editable" /></el-form-item>
              <el-form-item>
                <template #label><HelpTip term="类型别名">{{ $t("类型") }}</HelpTip></template>
                <el-input :model-value="phaseTypeLabel(selected)" disabled />
              </el-form-item>
              <el-form-item>
                <template #label><HelpTip term="执行类">{{ $t("执行类") }}</HelpTip></template>
                <el-input :model-value="executionKindLabel(executionKind(selected.type))" disabled />
              </el-form-item>
              <el-form-item>
                <template #label><HelpTip term="程序号">{{ $t("程序号") }}</HelpTip></template>
                <el-input-number
                  v-if="!isHostStepType(selected.type)"
                  :model-value="selected.plcProgramId ?? resolvePlcProgram(selected.type)"
                  :min="1"
                  :max="99"
                  :disabled="!editable"
                  @update:model-value="(v: number | undefined) => { if (selected) selected.plcProgramId = v ?? resolvePlcProgram(selected.type); }"
                />
                <span v-else class="muted">{{ $t("上位机工步，禁止写 PLC") }}</span>
              </el-form-item>
              <el-form-item>
                <template #label><HelpTip term="Unit Procedure">{{ $t("单元规程") }}</HelpTip></template>
                <el-input v-model="selected.unitProcedure" :disabled="!editable" :placeholder="$t('必填，如 UP-01 加工单元')" />
              </el-form-item>
              <el-form-item>
                <template #label><HelpTip term="Operation">{{ $t("操作") }}</HelpTip></template>
                <el-input v-model="selected.operation" :disabled="!editable" :placeholder="$t('必填，如 OP-Heat 升温')" />
              </el-form-item>
              <el-form-item :label="$t('说明')"><el-input v-model="selected.description" :disabled="!editable" /></el-form-item>
              <el-form-item :label="$t('看门狗（秒）')"><el-input-number v-model="selected.watchdogSeconds" :min="5" :disabled="!editable" /></el-form-item>
            </el-form>
          </div>
          <p v-else class="none-note">{{ $t("先在左侧工步列表或画布上选一个工步，这里会显示它的字段。") }}</p>
          <!-- 审核节点不受"是否选中工步"影响：审核人打开页面第一眼要看的就是卡在哪个节点。 -->
          <el-divider />
          <div class="muted" v-for="a in working?.approvals ?? []" :key="a.id">
            <b>{{ a.title || nodeLabel(a.node) }}</b> · {{ approvalDecisionLabel(a.decision) }} · {{ a.reviewerName ?? $t("待审") }}
            <div>{{ a.meaning }}</div>
            <div v-if="a.comment">{{ $t("意见：{0}", [a.comment]) }}</div>
          </div>
        </el-card>
      </el-col>
    </el-row>

    <!-- 参数槽位有 9 列（含单位与三个开关），以前塞在 span 8 的右栏里（~390px），
         后四列必须横向滚动才看得到；整宽放下后不需要滚动。 -->
    <el-card class="gap-before">
      <template #header>
        {{ $t("参数槽位") }}<span v-if="selected" class="muted"> · {{ $t("当前工步 {0} {1}", [selected.code, selected.name]) }}</span>
      </template>
      <template v-if="selected">
        <el-table :data="selected.parameters" size="small" max-height="420">
          <el-table-column prop="slotIndex" :label="$t('槽')" width="56" fixed />
          <el-table-column :label="$t('参数')" min-width="180">
            <template #default="{ row }"><el-input v-model="row.name" :disabled="!editable" /></template>
          </el-table-column>
          <el-table-column :label="$t('设定值')" width="104">
            <template #default="{ row }"><el-input-number v-model="row.setpoint" :controls="false" :disabled="!editable" /></template>
          </el-table-column>
          <el-table-column :label="$t('下限')" width="88">
            <template #default="{ row }"><el-input-number v-model="row.min" :controls="false" :disabled="!editable" /></template>
          </el-table-column>
          <el-table-column :label="$t('上限')" width="88">
            <template #default="{ row }"><el-input-number v-model="row.max" :controls="false" :disabled="!editable" /></template>
          </el-table-column>
          <el-table-column :label="$t('单位')" width="88">
            <template #default="{ row }"><el-input v-model="row.engineeringUnit" :disabled="!editable" /></template>
          </el-table-column>
          <el-table-column width="76">
            <template #header><HelpTip term="写PLC" /></template>
            <template #default="{ row }"><el-switch v-model="row.writeToPlc" :disabled="!editable" /></template>
          </el-table-column>
          <el-table-column width="68">
            <template #header><HelpTip term="质检" /></template>
            <template #default="{ row }"><el-switch v-model="row.archiveAsQuality" :disabled="!editable" /></template>
          </el-table-column>
          <el-table-column width="96">
            <template #header><HelpTip term="随批缩放" /></template>
            <template #default="{ row }"><el-switch v-model="row.scaleWithBatch" :disabled="!editable" /></template>
          </el-table-column>
          <el-table-column width="116">
            <template #header><HelpTip term="语义" /></template>
            <template #default="{ row }">
              <el-select v-model="row.semantic" :disabled="!editable" size="small">
                <el-option
                  v-for="s in parameterSemanticOptions"
                  :key="s"
                  :label="parameterSemanticLabel(s)"
                  :value="s"
                />
              </el-select>
            </template>
          </el-table-column>
          <el-table-column :label="$t('实测点')" min-width="130">
            <template #header><HelpTip term="实测点" /></template>
            <template #default="{ row }">
              <el-input
                v-model="row.measuredTag"
                :disabled="!editable || !row.archiveAsQuality"
                :placeholder="measuredTagRequired(row.semantic)
                  ? $t('实测值必须填：点表 Measured 的键名')
                  : $t('留空按名称推断')"
              />
            </template>
          </el-table-column>
        </el-table>
        <el-button class="gap-before-sm" size="small" :disabled="!editable" @click="addParam">{{ $t("新增参数槽") }}</el-button>
        <el-button class="gap-before-sm" size="small" :disabled="!editable" @click="removeLastParam">{{ $t("删除末槽") }}</el-button>
      </template>
      <p v-else class="none-note">{{ $t("未选择工步：参数槽位跟着工步走，先在左侧或画布上选一个。") }}</p>
    </el-card>
    <el-card class="gap-before" :header="$t('控制参数矩阵（工步 × 设定值）')">
      <SetpointMatrix
        :steps="working?.steps ?? []"
        :selected-id="selectedId"
        :readonly="!editable"
        @select="selectedId = $event"
      />
    </el-card>

    <RecipeHeaderDialog v-model="headerVisible" :recipe="detail" @saved="load" />
    <ParallelUnitDialog
      v-model="parallelVisible"
      :classes="phaseClasses"
      :default-class-id="paletteClassId"
      :suggested-unit="nextParallelUnitName"
      @add="addParallelUnit"
      @open-library="openLibrary"
    />
    <RecipeDiffDialog v-model="diffVisible" :diff="diff" />
  </div>
</template>

<script setup lang="ts">
import { t } from "../../i18n";
import { computed, markRaw, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { onBeforeRouteLeave, useRoute, useRouter } from "vue-router";
import { VueFlow, type NodeComponent } from "@vue-flow/core";
import { Background } from "@vue-flow/background";
import { Controls } from "@vue-flow/controls";
import "@vue-flow/core/dist/style.css";
import "@vue-flow/core/dist/theme-default.css";
import "@vue-flow/controls/dist/style.css";
import { ElMessage, ElMessageBox } from "element-plus";
import {
  compareVersions, createRecipeVersion, decideRecipe, getRecipeDetail, reopenRecipe, saveProcedure,
  submitRecipe, useApprovalChain
} from "../../api/recipes";
import { listApprovalChains } from "../../api/approvalChains";
import { listEquipmentClasses } from "../../api/equipment";
import { esignPassword, esignWithReason } from "../../utils/esign";
import type {
  ApprovalChainDto, EquipmentClassDto, ParameterDto, PhaseTemplateDto, RecipeDetailDto, RecipeVersionDiffDto, RecipeVersionDto, StepDto, StepType
} from "../../api/types";
import {
  recipeStatusLabel as statusLabel, approvalNodeLabel as nodeLabel, approvalDecisionLabel,
  measuredTagRequired,
  stepTypeLabel, executionKindLabel, phaseTypeLabel, parameterSemanticLabel, parameterSemanticOptions,
  userRoleLabel,
  esignMeaning
} from "../../utils/labels";
import { useLoad } from "../../utils/useLoad";
import HelpTip from "../../components/HelpTip.vue";
import { useAuthStore } from "../../stores/auth";
import { usePageShortcuts } from "../../shortcuts/registry";
import StepFlowNode from "../../components/StepFlowNode.vue";
import SetpointMatrix from "../../components/SetpointMatrix.vue";
import RecipeHeaderDialog from "../../components/RecipeHeaderDialog.vue";
import ParallelUnitDialog from "../../components/ParallelUnitDialog.vue";
import RecipeDiffDialog from "../../components/RecipeDiffDialog.vue";
import { DEFAULT_UNIT_LANE, LAYOUT_X0, LAYOUT_Y0, unitLane } from "../../procedureLayout";
import { executionKind, hostStepTypes, isHostStepType, resolvePlcProgram } from "../../utils/plcProgram";
import { templateProgram } from "../../utils/phaseTemplate";
import { canDecideReview, headPendingNode } from "../../utils/reviewGate";
import { defaultOperation, defaultParameters, MAX_PARAM_SLOTS } from "../../utils/procedureDefaults";
import {
  JOIN_RULE_HINT, canJoinSteps, invalidPlcProgram, missingIsa88Name, nextStepCode, terminalSteps
} from "../../utils/procedureTopology";
import { useProcedureLanes } from "../../utils/useProcedureLanes";
import { useFlowCanvas } from "../../utils/useFlowCanvas";

/**
 * 配方设计态。
 *
 * 泳道与设备类绑定在 utils/useProcedureLanes，画布映射在 utils/useFlowCanvas，
 * 拓扑与保存前校验在 utils/procedureTopology（纯函数、有单测），
 * 缺省工艺内容在 utils/procedureDefaults，三个对话框各自成件。
 * 页面留的是：草稿对象本身、选中的工步、"未保存"脏检查，以及六个签名/请求动作。
 */
const { loading, error } = useLoad();
const route = useRoute();
const router = useRouter();
const auth = useAuthStore();
const detail = ref<RecipeDetailDto | null>(null);
const selectedId = ref<string | null>(null);
const viewingId = ref<string | null>(null);
const phaseClasses = ref<EquipmentClassDto[]>([]);
const diff = ref<RecipeVersionDiffDto | null>(null);
const diffVisible = ref(false);
const headerVisible = ref(false);
const parallelVisible = ref(false);
const edgeFromId = ref("");
const edgeToId = ref("");
const saving = ref(false);
const submitting = ref(false);
const busyAction = ref("");
const nodeTypes = { recipeStep: markRaw(StepFlowNode) as unknown as NodeComponent };

/** 草稿优先，其次按版本条选中的那个版本浏览。 */
const working = computed<RecipeVersionDto | null>(() => {
  const versions = detail.value?.versions ?? [];
  return versions.find((v) => v.id === viewingId.value)
    ?? detail.value?.draft
    ?? detail.value?.approved
    ?? null;
});
const selected = computed(() => working.value?.steps.find((s) => s.id === selectedId.value) ?? null);
const canAuthor = computed(() => auth.is("ProcessEngineer"));
const editable = computed(() => canAuthor.value && working.value?.status === "Draft");
const canDecide = computed(() => canDecideReview(working.value, auth.user?.role));

const lanes = useProcedureLanes({ working, selected, phaseClasses, editable });
const {
  paletteClassId, paletteClass, laneNames, currentLane, laneClassCode,
  rememberLaneClass, stampLaneClass, hydrateLaneClasses, selectLane, classIdOf
} = lanes;
const paletteTemplates = computed(() => paletteClass.value?.templates ?? []);

function programHint(step: StepDto) {
  if (isHostStepType(step.type)) return "";
  return ` · ${t("程序 {0}", resolvePlcProgram(step.type, step.plcProgramId))}`;
}

function canJoin(fromId: string, toId: string) {
  const steps = working.value?.steps ?? [];
  const from = steps.find((s) => s.id === fromId);
  const to = steps.find((s) => s.id === toId);
  return !!from && !!to && canJoinSteps(steps, from, to);
}

function warnJoinRule() {
  ElMessage.warning(t(JOIN_RULE_HINT));
}

const canvas = useFlowCanvas({ working, selectedId, editable, canJoin, onConnectBlocked: warnJoinRule });
const { nodes, edges, flowHeight, edgeRows, layoutWorking, autoLayout, onNodes, onConnect, onNodeClick } = canvas;

/** 脏检查用整个草稿的工艺内容做指纹：任何一格参数变了都算未保存。 */
const savedStamp = ref("");
function procedureStamp(v: RecipeVersionDto | null) {
  return v ? JSON.stringify({ steps: v.steps, edges: v.edges }) : "";
}
function markClean() {
  savedStamp.value = procedureStamp(working.value);
}
const dirty = computed(() => editable.value && procedureStamp(working.value) !== savedStamp.value);

async function confirmDiscard(): Promise<boolean> {
  if (!dirty.value) return true;
  try {
    await ElMessageBox.confirm("工艺有未保存的修改，继续将丢弃这些改动。", "未保存修改", {
      type: "warning",
      confirmButtonText: "丢弃并离开",
      cancelButtonText: "留下"
    });
    return true;
  } catch {
    return false;
  }
}

async function viewVersion(id: string) {
  if (id === viewingId.value) return;
  if (!(await confirmDiscard())) return;
  viewingId.value = id;
  await nextTick();
  markClean();
}

async function load() {
  try {
    detail.value = await getRecipeDetail(String(route.params.id ?? ""));
    const versions = detail.value.versions ?? [];
    if (!viewingId.value || !versions.some((v) => v.id === viewingId.value))
      viewingId.value = detail.value.draft?.id ?? detail.value.approved?.id ?? versions[0]?.id ?? null;
    error.value = "";
    await nextTick();
    markClean();
  } catch (e) {
    // 原先取数失败会留下空白页且无任何提示（根节点 v-if="detail"），这里显式报错。
    error.value = (e as Error).message || "配方详情加载失败";
  } finally {
    loading.value = false;
  }
}

function openLibrary(classId?: string) {
  const cls = phaseClasses.value.find((c) => c.id === (classId || paletteClassId.value))?.code
    || paletteClass.value?.code;
  void router.push({
    path: "/equipment",
    query: { tab: "library", ...(cls ? { class: cls } : {}) }
  });
}

function onPaletteClassChange(id: string) {
  const lane = currentLane.value;
  if (!lane || !id) return;
  rememberLaneClass(lane, id);
  stampLaneClass(lane, id);
}

function pickLane(lane: string) {
  const id = selectLane(lane);
  if (id) selectedId.value = id;
}

watch(working, (v) => {
  if (v?.steps.length && !v.steps.some((s) => s.id === selectedId.value))
    selectedId.value = v.steps[0].id;
}, { immediate: true });

function addStep(type: StepType, opts?: {
  unitProcedure?: string;
  chain?: boolean;
  name?: string;
  operation?: string;
  watchdogSeconds?: number;
  plcProgramId?: number | null;
  equipmentClassCode?: string | null;
  parameters?: ParameterDto[];
}) {
  if (!working.value || working.value.status !== "Draft") return;
  const n = working.value.steps.length;
  const last = working.value.steps[n - 1];
  const id = crypto.randomUUID();
  const unit = opts?.unitProcedure?.trim()
    || selected.value?.unitProcedure?.trim()
    || last?.unitProcedure
    || DEFAULT_UNIT_LANE;
  const chain = opts?.chain !== false;
  const classCode = opts?.equipmentClassCode?.trim()
    || paletteClass.value?.code
    || selected.value?.equipmentClassCode
    || undefined;
  working.value.steps.push({
    id,
    code: nextStepCode(n),
    name: opts?.name?.trim() || stepTypeLabel(type),
    type,
    ordinal: n,
    canvasX: LAYOUT_X0,
    canvasY: LAYOUT_Y0,
    watchdogSeconds: opts?.watchdogSeconds && opts.watchdogSeconds > 0 ? opts.watchdogSeconds : 120,
    description: "",
    unitProcedure: unit,
    operation: opts?.operation?.trim() || defaultOperation(type),
    plcProgramId: isHostStepType(type) ? null : (opts?.plcProgramId ?? resolvePlcProgram(type)),
    equipmentClassCode: classCode ?? null,
    parameters: opts?.parameters ?? defaultParameters(type)
  });
  if (classCode) {
    const owner = classIdOf(classCode);
    if (owner) rememberLaneClass(unitLane(unit), owner);
  }
  if (n > 0 && chain && last)
    working.value.edges.push({ fromStepId: last.id, toStepId: id });
  const placed = layoutWorking()[id];
  const added = working.value.steps[working.value.steps.length - 1];
  if (placed && added) {
    added.canvasX = placed.x;
    added.canvasY = placed.y;
  }
  selectedId.value = id;
}

function addFromTemplate(templateId: string, opts?: { unitProcedure?: string; chain?: boolean; equipmentClassCode?: string }) {
  const template = phaseClasses.value.flatMap((c) => c.templates).find((t) => t.id === templateId);
  if (!template) return;
  const owner = phaseClasses.value.find((c) => c.templates.some((t) => t.id === templateId));
  addStep(template.stepType, {
    name: template.name,
    operation: template.operation,
    watchdogSeconds: template.watchdogSeconds,
    plcProgramId: isHostStepType(template.stepType) ? null : templateProgram(template),
    unitProcedure: opts?.unitProcedure,
    chain: opts?.chain,
    equipmentClassCode: opts?.equipmentClassCode ?? owner?.code ?? paletteClass.value?.code,
    parameters: template.parameters.map(toParameter)
  });
}

function toParameter(p: PhaseTemplateDto["parameters"][number]): ParameterDto {
  return {
    slotIndex: p.slotIndex,
    name: p.name,
    engineeringUnit: p.engineeringUnit,
    setpoint: p.setpoint,
    min: p.min,
    max: p.max,
    writeToPlc: p.writeToPlc,
    archiveAsQuality: p.archiveAsQuality,
    scaleWithBatch: p.scaleWithBatch ?? false,
    semantic: p.semantic ?? "Unspecified",
    measuredTag: p.measuredTag ?? null
  };
}

const nextParallelUnitName = computed(() => `UP-${String(laneNames.value.length + 1).padStart(2, "0")} 并行单元`);

function addParallelUnit(payload: { unit: string; classId: string; templateId: string }) {
  addFromTemplate(payload.templateId, {
    unitProcedure: payload.unit,
    chain: false,
    equipmentClassCode: phaseClasses.value.find((c) => c.id === payload.classId)?.code
  });
  if (payload.classId) {
    paletteClassId.value = payload.classId;
    rememberLaneClass(unitLane(payload.unit), payload.classId);
  }
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
  if (!canJoin(from, to)) {
    warnJoinRule();
    return;
  }
  working.value.edges.push({ fromStepId: from, toStepId: to });
}

function joinToSelected() {
  if (!working.value || !editable.value || !selectedId.value) return;
  const to = selectedId.value;
  const target = working.value.steps.find((s) => s.id === to);
  if (!target) return;
  const terminals = terminalSteps(working.value.steps, working.value.edges, to);
  if (!terminals.length) {
    ElMessage.info("没有可汇合的末工步（所有工步已有后继）");
    return;
  }
  const joined: string[] = [];
  const skipped: string[] = [];
  for (const step of terminals) {
    if (working.value.edges.some((e) => e.fromStepId === step.id && e.toStepId === to))
      continue;
    if (!canJoin(step.id, to)) {
      skipped.push(step.code);
      continue;
    }
    working.value.edges.push({ fromStepId: step.id, toStepId: to });
    joined.push(step.code);
  }
  if (joined.length)
    ElMessage.success(t("已将 {0} 汇合到当前工步", joined.join("、")));
  if (skipped.length)
    ElMessage.warning(t("未汇合 {0}：{1}", skipped.join("、"), t(JOIN_RULE_HINT)));
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

function addParam() {
  if (!selected.value || selected.value.parameters.length >= MAX_PARAM_SLOTS) return;
  selected.value.parameters.push({
    slotIndex: selected.value.parameters.length,
    name: "Param",
    engineeringUnit: "",
    setpoint: 0,
    min: null,
    max: null,
    writeToPlc: true,
    archiveAsQuality: false,
    scaleWithBatch: false,
    semantic: "Unspecified",
    measuredTag: null
  });
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
    diff.value = await compareVersions(detail.value.id, from, to);
    diffVisible.value = true;
  } catch (e) {
    ElMessage.error((e as Error).message);
  }
}

/** 签名动作的外壳：置忙 → 提交 → 重拉 → 复位。取消签名抛的是字符串 "cancel"，不算失败。 */
async function runAction(name: string, action: (id: string) => Promise<void>) {
  if (busyAction.value) return;
  const id = detail.value?.id;
  if (!id) return;
  busyAction.value = name;
  try {
    await action(id);
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    busyAction.value = "";
  }
}

// ---- 本配方走哪条审批链 ----
// chainCode 是 detail 的派生值而不是本地 ref：改完由 load() 重新拉，
// 用户在签名框里取消或后端拒绝时选择会自动回到原值，不需要手写回滚。
// 哨兵而不是 ""：el-select 把空串当未选，会用 placeholder 盖掉"默认链"这一项。
const DEFAULT_CHAIN = "__default__";
const chains = ref<ApprovalChainDto[]>([]);
const chainSaving = ref(false);
const chainCode = computed(() => detail.value?.approvalChainCode || DEFAULT_CHAIN);
const defaultChain = computed(() => chains.value.find(c => c.isDefault) ?? null);
const defaultChainLabel = computed(() => {
  const c = defaultChain.value;
  return c ? t("默认链：{0} · {1}", c.name, chainPath(c)) : t("默认链（管理员未配置）");
});
const chainPath = (c: ApprovalChainDto) =>
  c.steps.map(s => `${s.title}（${userRoleLabel(s.requiredRole)}）`).join(" → ");

async function pickChain(next: string | number | boolean | undefined) {
  const code = next === DEFAULT_CHAIN || next === "" || next == null ? null : String(next);
  if (code === (detail.value?.approvalChainCode ?? null)) return;
  const target = code ? chains.value.find(c => c.code === code) : defaultChain.value;
  chainSaving.value = true;
  try {
    const password = await esignPassword(t("改审批链"),
      t("确认这份配方之后提交时走「{0}」。在审版本已冻结，不受影响。", target?.name ?? t("默认链")));
    await useApprovalChain(String(route.params.id ?? ""), code, password);
    ElMessage.success(t("审批链已更新"));
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    chainSaving.value = false;
  }
}

// 整条链在提交时就展开完了，所以"取头节点"必须按 seq 比；这个 computed 同时喂标题与含义，
// 两处各自 find(Pending) 会在排序变化时指到不同节点。
const pendingNode = computed(() => headPendingNode(working.value));
const pendingLevelLabel = computed(() =>
  pendingNode.value ? t("当前节点：{0}", pendingNode.value.title || nodeLabel(pendingNode.value.node)) : t("审核"));
const pendingMeaning = computed(() =>
  pendingNode.value?.meaning ?? "请再次输入登录密码作为电子签名。"
);

/**
 * 保存工艺。返回是否真的存成，供「提交审核」决定要不要继续。
 * 两道校验（ISA-88 名称、程序号）都在本地先拦，规则与后端同源（utils/procedureTopology）。
 */
async function save(): Promise<boolean> {
  if (!detail.value || !working.value || saving.value) return false;
  const missing = missingIsa88Name(working.value.steps);
  if (missing) {
    ElMessage.warning(missing);
    return false;
  }
  const invalid = invalidPlcProgram(working.value.steps);
  if (invalid) {
    ElMessage.warning(invalid);
    return false;
  }
  saving.value = true;
  try {
    // 变更原因与签名合并成一屏（与审核台 utils/esign.ts 同一个控件）：
    // 串联两个弹窗时，第二屏不再复述"签的是哪份变更"。
    const { reason: changeReason, password } = await esignWithReason(
      "保存工艺",
      esignMeaning("recipe.procedure.esign"),
      "变更原因",
      true
    );
    await saveProcedure(detail.value.id, {
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
    if (!(await save())) return;
    const password = await esignPassword(t("提交审核"), esignMeaning("recipe.submit.esign"));
    await submitRecipe(detail.value.id, password);
    ElMessage.success("已电子签名并提交多级审核（工艺主管 → 质量）");
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    submitting.value = false;
  }
}

function decide(decision: "Approved" | "Rejected") {
  return runAction(decision === "Approved" ? "decide" : "reject", async (id) => {
    const { reason: comment, password } = await esignWithReason(
      pendingLevelLabel.value,
      pendingMeaning.value,
      decision === "Approved" ? t("审核意见（通过）") : t("驳回原因"),
      decision === "Rejected"
    );
    await decideRecipe(id, { decision, comment, password });
    await load();
  });
}

function reopen() {
  return runAction("reopen", async (id) => {
    const password = await esignPassword(
      "重新打开驳回版本",
      esignMeaning("recipe.reopen.esign")
    );
    await reopenRecipe(id, password);
    await load();
    ElMessage.success(t("已重新打开为草稿，可改工艺后再次提交审核。"));
  });
}

function newVersion() {
  return runAction("version", async (id) => {
    const { reason: changeNote, password } = await esignWithReason(
      "升版",
      esignMeaning("recipe.new-version.esign"),
      "变更说明",
      false
    );
    await createRecipeVersion(id, changeNote, password);
    await load();
    viewingId.value = detail.value?.draft?.id ?? viewingId.value;
    ElMessage.success(t("已创建草稿 v{0}，在此版本改工艺后再提交审核。", detail.value?.draft?.versionNumber ?? ""));
  });
}

usePageShortcuts(() => [
  {
    id: "recipe.save",
    chord: "ctrl+s",
    group: "配方设计",
    label: "保存工艺",
    allowInInput: true,
    when: () => !!canAuthor.value && working.value?.status === "Draft" && !saving.value && !submitting.value,
    run: () => { void save(); }
  },
  {
    id: "recipe.primary",
    chord: "ctrl+enter",
    group: "配方设计",
    label: canDecide.value ? "通过并电子签名" : "提交审核",
    allowInInput: true,
    when: () =>
      !busyAction.value
      && ((canDecide.value) || (!!canAuthor.value && working.value?.status === "Draft" && !submitting.value)),
    run: () => {
      if (canDecide.value) void decide("Approved");
      else void submit();
    }
  }
]);

function onBeforeUnload(e: BeforeUnloadEvent) {
  if (!dirty.value) return;
  e.preventDefault();
  e.returnValue = "";
}

onMounted(async () => {
  await load();
  phaseClasses.value = await listEquipmentClasses();
  chains.value = await listApprovalChains();
  hydrateLaneClasses();
  window.addEventListener("beforeunload", onBeforeUnload);
});

onBeforeRouteLeave(async () => {
  if (!dirty.value) return true;
  return await confirmDiscard();
});

onUnmounted(() => {
  window.removeEventListener("beforeunload", onBeforeUnload);
});
</script>

<style scoped>
.palette { display: flex; gap: var(--space-2); flex-wrap: wrap; margin-bottom: var(--space-2); align-items: center; }
.palette-label { font-size: 11px; color: var(--muted); }
.chain-pick { width: 232px; margin-left: var(--space-2); vertical-align: middle; }
.step-item { padding: var(--space-2); border-radius: 8px; cursor: pointer; border: 1px solid transparent; margin-bottom: var(--space-2); }
.step-item.on { background: var(--tint); border-color: var(--accent); }
.muted { color: var(--muted); font-size: 12px; }
.versions { display: flex; gap: var(--space-2); flex-wrap: wrap; margin: 0 0 var(--space-3); }
.ver {
  border: 1px solid var(--line); background: var(--panel); color: var(--text-body);
  border-radius: 999px; padding: var(--space-1) var(--space-3); cursor: pointer;
}
.ver.on { border-color: var(--accent); background: var(--tint); color: var(--text); }
.unsaved { color: var(--warn); font-size: 14px; font-weight: 600; }
.lanes { display: flex; gap: var(--space-2); flex-wrap: wrap; margin-bottom: var(--space-2); }
.lane-chip {
  font-size: 11px; color: var(--muted); border: 1px dashed var(--accent);
  border-radius: 999px; padding: 2px var(--space-3); min-height: 24px;
  background: transparent; cursor: pointer; font-family: inherit;
}
.lane-chip.on { color: var(--text); background: var(--tint); border-style: solid; }
.edge-editor { margin-top: var(--space-3); }
.edge-row { display: flex; align-items: center; justify-content: space-between; font-size: 12px; color: var(--text-body); }
.edge-add { display: flex; align-items: center; gap: var(--space-2); margin-top: var(--space-2); flex-wrap: wrap; }
.flow-canvas {
  /* 实际高度由 flowHeight 按内容算（见 useFlowCanvas），这里只保底 */
  min-height: 240px;
  border: 1px solid var(--line);
  border-radius: 8px;
  overflow: hidden;
  background: var(--sunken);
}
/* 参数表里的数字控件：el-input-number 默认 150px 宽，塞进窄列会被单元格 overflow 裁掉
   （实测「设定值 12」只露一半、下限/上限看着是空框）。让控件跟随列宽。 */
.el-table :deep(.el-input-number) { width: 100%; }
</style>
