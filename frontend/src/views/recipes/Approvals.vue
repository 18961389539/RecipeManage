<template>
  <div class="approvals">
    <div class="page-title">
      <div>
        <h2>{{ $t("多级审核工作台") }}<PageGuideButton guide-key="approvals" /></h2>
        <span>{{ $t("按配方选定的审批链逐级签署；当前角色只能处理轮到自己的那一级。链由管理员在「审批链配置」里维护。") }}</span>
      </div>
      <div>
        <el-button :loading="loading" @click="loadList">{{ $t("刷新") }}</el-button>
      </div>
    </div>
    <div class="filter-bar">
      <HelpTip term="聚焦本页搜索" chord="/" plain placement="bottom">
        <el-input v-model="query" class="search-field" clearable data-shortcut-search :placeholder="$t('搜索编码 / 名称 / 产品')" />
      </HelpTip>
      <span v-if="!loading" class="result-count">{{ countText }}</span>
    </div>
    <el-alert class="gap-after"
      v-if="error"
      :closable="false"
      type="error"
      :title="$t('待审核配方加载失败：{0}', [error])"
      show-icon
     
    />
    <el-row :gutter="12">
      <el-col :span="8" :xs="24">
        <el-card :header="$t('待审核配方')">
          <el-table
            ref="tableRef"
            :data="items"
            v-loading="loading"
            class="clickable-rows"
            :empty-text="query.trim() ? $t('没有匹配的待审配方') : $t('没有待审核配方')"
            highlight-current-row
            :row-class-name="rowClass"
            @row-click="(row: RecipeListItemDto) => select(row.id)"
          >
            <el-table-column prop="code" :label="$t('编码')" width="110" fixed />
            <el-table-column prop="name" :label="$t('名称')" min-width="120" />
            <el-table-column :label="$t('待审节点')" width="110">
              <template #default="{ row }">{{ row.pendingTitle || "—" }}</template>
            </el-table-column>
            <el-table-column :label="$t('审版')" width="56">
              <template #default="{ row }">{{ row.reviewVersion ? `v${row.reviewVersion}` : "—" }}</template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
      <el-col :span="16" :xs="24">
        <el-empty v-if="!detail && !detailLoading" :description="$t('选择左侧配方，在此审阅工艺与冻结设定矩阵')" />
        <template v-else-if="detail">
          <el-card v-loading="detailLoading">
            <template #header>
              <div class="head">
                <div>
                  <b>{{ detail.code }} {{ detail.name }}</b>
                  <div class="muted">{{ $t("产品 {0} · 审核版 v{1} · 状态 {2}", [detail.productName, review?.versionNumber ?? "—", recipeStatusLabel(review?.status)]) }}</div>
                </div>
                <div>
                  <HelpTip v-if="canDecide" term="通过并电子签名" chord="ctrl+enter" allow-in-input plain placement="bottom">
                    <el-button type="success" :loading="deciding === 'Approved'" :disabled="!canApprove" @click="decide('Approved')">{{ $t("通过并电子签名") }}</el-button>
                  </HelpTip>
                  <HelpTip v-if="canDecide" term="驳回" plain placement="bottom">
                    <el-button type="danger" :loading="deciding === 'Rejected'" :disabled="!!deciding" @click="decide('Rejected')">{{ $t("驳回") }}</el-button>
                  </HelpTip>
                  <el-button @click="$router.push(`/recipes/${detail.id}`)">{{ $t("打开设计器") }}</el-button>
                </div>
              </div>
            </template>
            <el-alert class="gap-after"
              v-if="pending"
              :closable="false"
              show-icon
              :type="canDecide ? 'warning' : 'info'"
              :title="$t('当前节点：{0}', [pending.title || nodeLabel(pending.node)])"
              :description="pending.meaning || pendingMeaningFallback"
             
            />
            <div v-if="comparisonRequired && diffLoading" class="diff-status">
              <el-alert
                :closable="false"
                type="info"
                :title="$t('正在加载版本差异…')"
                show-icon
              />
            </div>
            <div v-else-if="comparisonRequired && diffError" class="diff-error">
              <el-alert
                class="diff-error-message"
                :closable="false"
                type="error"
                :title="$t('版本差异加载失败，未展示完整差异，暂不能通过审核。')"
                show-icon
              />
              <el-button size="small" :loading="diffLoading" @click="retryDiff">{{ $t("重试加载差异") }}</el-button>
            </div>
            <el-alert class="gap-after"
              v-if="!pending"
              :closable="false"
              type="success"
              :title="$t('本版本已无待审节点')"
             
            />
            <p v-if="review?.changeNote" class="muted">{{ $t("变更说明：{0}", [review.changeNote]) }}</p>
            <div v-if="diff" class="diff-block">
              <h4>{{ $t("相对生效版 v{0} 的差异", [diff.fromVersion]) }}</h4>
              <p v-if="diff.addedSteps.length">{{ $t("新增工步：{0}", [diff.addedSteps.join("、")]) }}</p>
              <p v-if="diff.removedSteps.length">{{ $t("删除工步：{0}", [diff.removedSteps.join("、")]) }}</p>
              <el-table v-if="diff.changes.length" :data="diff.changes" size="small" max-height="220" border>
                <el-table-column prop="path" :label="$t('路径')" min-width="180" fixed />
                <el-table-column prop="before" :label="$t('生效版')" />
                <el-table-column prop="after" :label="$t('审核版')" />
              </el-table>
              <el-empty
                v-else-if="!diff.addedSteps.length && !diff.removedSteps.length"
                :description="$t('与生效版工艺内容相同')"
              />
            </div>
            <h4>{{ $t("工艺工步") }}</h4>
            <ProcedureFlow
              v-if="review"
              flow-id="approval-flow"
              :steps="flowSteps"
              :edges="review.edges"
              :selected-step-id="selectedStepId"
              :markers="flowMarkers"
              :height="260"
              @select="selectedStepId = $event"
            />
            <h4>{{ $t("参数矩阵（只读审阅）") }}</h4>
            <SetpointMatrix
              v-if="review"
              :steps="review.steps"
              :selected-id="selectedStepId"
              :changed-keys="matrixChanged"
              readonly
              :max-height="280"
              @select="selectedStepId = $event"
            />
            <h4><HelpTip term="电子签名">{{ $t("电子签名链") }}</HelpTip></h4>
            <el-table :data="review?.approvals ?? []" size="small" border>
              <el-table-column :label="$t('审核节点')" width="140" fixed>
                <template #default="{ row }">{{ row.title || nodeLabel(row.node) }}</template>
              </el-table-column>
              <el-table-column :label="$t('要求角色')" width="100">
                <template #default="{ row }">{{ userRoleLabel(row.requiredRole) }}</template>
              </el-table-column>
              <el-table-column prop="decision" :label="$t('结论')" width="120">
                <template #default="{ row }">
                  <el-tag size="small" :type="approvalDecisionTagType(row.decision)" effect="dark">{{ approvalDecisionLabel(row.decision) }}</el-tag>
                </template>
              </el-table-column>
              <el-table-column prop="reviewerName" :label="$t('签署人')" />
              <el-table-column prop="meaning" :label="$t('签署含义')" min-width="240" />
              <el-table-column :label="$t('签署时间')" width="170">
                <template #default="{ row }">{{ formatDateTime(row.decidedAt) }}</template>
              </el-table-column>
              <el-table-column prop="comment" :label="$t('意见')" />
            </el-table>
          </el-card>
        </template>
      </el-col>
    </el-row>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { useRoute } from "vue-router";
import { ElMessage } from "element-plus";
import type { TableInstance } from "element-plus";
import http from "../../api/http";
import { esignWithReason } from "../../utils/esign";
import type { ApprovalDto, RecipeDetailDto, RecipeListItemDto, RecipeVersionDiffDto, RecipeVersionDto } from "../../api/types";
import { useAuthStore } from "../../stores/auth";
import ProcedureFlow from "../../components/ProcedureFlow.vue";
import SetpointMatrix from "../../components/SetpointMatrix.vue";
import { changedMatrixKeys } from "../../setpointMatrix";
import {
  approvalDecisionLabel,
  approvalDecisionTagType,
  approvalNodeLabel as nodeLabel,
  recipeStatusLabel,
  userRoleLabel
} from "../../utils/labels";
import { formatDateTime, matchesQuery } from "../../utils/format";
import { canDecideReview, headPendingNode } from "../../utils/reviewGate";
import { t } from "../../i18n";
import { useLoad } from "../../utils/useLoad";
import { useKeyboardRows } from "../../utils/useKeyboardRows";
import HelpTip from "../../components/HelpTip.vue";
import { usePageShortcuts } from "../../shortcuts/registry";

const route = useRoute();
const auth = useAuthStore();
const all = ref<RecipeListItemDto[]>([]);
const query = ref("");
const items = computed(() => all.value.filter((r) => {
  if (r.draftStatus !== "InReview") return false;
  return matchesQuery(query.value, r.code, r.name, r.productName, r.productCode, r.pendingTitle);
}));
const countText = computed(() => {
  const total = all.value.filter((r) => r.draftStatus === "InReview").length;
  const n = items.value.length;
  // 计数走带占位的译文键，而不是拼好的模板字符串：英文里量词位置不同（"7 items"），
  // 字符串拼出来的那种在另一种语言里必然是错的。
  return query.value.trim() ? t("{0} / {1} 条", n, total) : t("共 {0} 条", total);
});
// 整行可点选中，但 EP 渲染的 tr 不可聚焦——键盘用户此前选不了待审配方。
const tableRef = ref<TableInstance>();
useKeyboardRows(tableRef, () => items.value);
const detail = ref<RecipeDetailDto | null>(null);
const selectedId = ref<string | null>(null);
const selectedStepId = ref<string | null>(null);
const diff = ref<RecipeVersionDiffDto | null>(null);
const diffLoading = ref(false);
const diffError = ref(false);
const { loading, error, run } = useLoad();
const detailLoading = ref(false);
const deciding = ref("");
let selectionRequestId = 0;
let diffRequestId = 0;

const review = computed<RecipeVersionDto | null>(() => {
  const versions = detail.value?.versions ?? [];
  return versions.find((v) => v.status === "InReview")
    ?? detail.value?.draft
    ?? null;
});

const pending = computed(() => headPendingNode(review.value));

const pendingMeaningFallback = "请再次输入登录密码作为电子签名。";

const canDecide = computed(() => canDecideReview(review.value, auth.user?.role));

const comparisonRequired = computed(() => {
  const approved = detail.value?.approved?.versionNumber;
  const reviewing = review.value?.versionNumber;
  return approved != null && reviewing != null && approved !== reviewing;
});

const canApprove = computed(() =>
  canDecide.value
  && !deciding.value
  && !detailLoading.value
  && !diffLoading.value
  && (!comparisonRequired.value || !!diff.value)
);

const flowMarkers = computed(() => {
  const map: Record<string, "added" | "changed"> = {};
  if (!diff.value) return map;
  for (const code of diff.value.changedStepCodes ?? [])
    map[code] = diff.value.addedSteps.includes(code) ? "added" : "changed";
  for (const code of diff.value.addedSteps)
    map[code] = "added";
  return map;
});

const matrixChanged = computed(() => changedMatrixKeys(review.value?.steps ?? [], diff.value));

const flowSteps = computed(() =>
  (review.value?.steps ?? []).map((s) => ({
    id: s.id,
    code: s.code,
    name: s.name,
    type: s.type,
    unitProcedure: s.unitProcedure,
    operation: s.operation,
    plcProgramId: s.plcProgramId,
    ordinal: s.ordinal
  }))
);

function rowClass({ row }: { row: RecipeListItemDto }) {
  return row.id === selectedId.value ? "current-row" : "";
}

async function loadList() {
  await run(http.get<RecipeListItemDto[]>("/recipes"), async (d) => {
    all.value = d;
    const q = typeof route.query.id === "string" ? route.query.id : null;
    const next = q && items.value.some((r) => r.id === q)
      ? q
      : (selectedId.value && items.value.some((r) => r.id === selectedId.value)
        ? selectedId.value
        : items.value[0]?.id ?? null);
    if (next) await select(next);
    else {
      selectedId.value = null;
      detail.value = null;
      diff.value = null;
    }
  });
}

async function select(id: string) {
  const requestId = ++selectionRequestId;
  diffRequestId++;
  selectedId.value = id;
  detailLoading.value = true;
  diffLoading.value = false;
  diffError.value = false;
  diff.value = null;
  try {
    const response = await http.get<RecipeDetailDto>(`/recipes/${id}`);
    if (requestId !== selectionRequestId) return;
    detail.value = response.data;
    selectedStepId.value = review.value?.steps[0]?.id ?? null;
    detailLoading.value = false;
    const approved = detail.value.approved?.versionNumber;
    const reviewing = review.value?.versionNumber;
    if (approved && reviewing && approved !== reviewing) {
      await loadDiff(id, approved, reviewing, requestId);
    }
  } catch (e) {
    if (requestId !== selectionRequestId) return;
    // 原先详情取数失败毫无反馈，右侧区域会一直停在"请选择左侧配方"。
    ElMessage.error(t("配方详情加载失败：{0}", (e as Error).message));
    detail.value = null;
    diff.value = null;
    diffError.value = false;
  } finally {
    if (requestId === selectionRequestId) detailLoading.value = false;
  }
}

async function loadDiff(id: string, fromVersion: number, toVersion: number, selectionId = selectionRequestId) {
  const requestId = ++diffRequestId;
  diffLoading.value = true;
  diffError.value = false;
  diff.value = null;
  try {
    const response = await http.get<RecipeVersionDiffDto>(`/recipes/${id}/compare`, {
      params: { fromVersion, toVersion }
    });
    if (requestId !== diffRequestId || selectionId !== selectionRequestId || selectedId.value !== id) return;
    diff.value = response.data;
  } catch {
    if (requestId !== diffRequestId || selectionId !== selectionRequestId || selectedId.value !== id) return;
    diffError.value = true;
  } finally {
    if (requestId === diffRequestId && selectionId === selectionRequestId) diffLoading.value = false;
  }
}

function retryDiff() {
  const id = selectedId.value;
  const approved = detail.value?.approved?.versionNumber;
  const reviewing = review.value?.versionNumber;
  if (!id || approved == null || reviewing == null || approved === reviewing) return;
  void loadDiff(id, approved, reviewing);
}

async function decide(decision: "Approved" | "Rejected") {
  if (!detail.value || deciding.value || (decision === "Approved" && !canApprove.value)) return;
  deciding.value = decision;
  try {
    const meaning = pending.value?.meaning ?? pendingMeaningFallback;
    const level = t("当前节点：{0}", pending.value?.title ?? "");
    // 驳回必须有原因（后端 REJECT_REASON），通过时意见可选——原来两栏分两个弹窗问。
    const { reason: comment, password } = await esignWithReason(
      level,
      meaning,
      decision === "Approved" ? "审核意见（通过）" : "驳回原因",
      decision === "Rejected"
    );
    await http.post(`/recipes/${detail.value.id}/decide`, { decision, comment, password });
    ElMessage.success(decision === "Approved" ? "已电子签名通过" : "已驳回");
    await loadList();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    deciding.value = "";
  }
}

usePageShortcuts(() => [
  {
    id: "approval.approve",
    chord: "ctrl+enter",
    group: "配方审核",
    label: "通过并电子签名",
    allowInInput: true,
    when: () => canApprove.value,
    run: () => { void decide("Approved"); }
  }
]);

onMounted(loadList);
</script>

<style scoped>
.head { display: flex; justify-content: space-between; gap: var(--space-3); align-items: flex-start; flex-wrap: wrap; }
.muted { color: var(--muted); font-size: 12px; margin: 0 0 var(--space-2); }
h4 { margin: var(--space-4) 0 var(--space-2); font-size: 14px; }
.diff-block { margin: var(--space-2) 0 var(--space-4); }
.diff-block p { color: var(--text-body); font-size: 13px; margin: 0 0 var(--space-2); }
.diff-status { margin: var(--space-2) 0 var(--space-4); }
.diff-error { display: flex; align-items: center; flex-wrap: wrap; gap: var(--space-2); margin: var(--space-2) 0 var(--space-4); }
.diff-error-message { flex: 1; min-width: 0; }
</style>
