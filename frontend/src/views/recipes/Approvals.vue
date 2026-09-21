<template>
  <div class="approvals">
    <div class="page-title">
      <div>
        <h2>多级审核工作台</h2>
        <span>工艺工程师提交 → 工艺主管签署路径 → 质量签署参数窗口。当前角色只能处理对应节点。</span>
      </div>
      <el-button :loading="loading" @click="loadList">刷新</el-button>
    </div>
    <el-alert class="gap-after"
      v-if="error"
      :closable="false"
      type="error"
      :title="`待审核配方加载失败：${error}`"
      show-icon
     
    />
    <el-row :gutter="12">
      <el-col :span="8" :xs="24">
        <el-card header="待审核配方">
          <el-table
            :data="items"
            v-loading="loading"
            class="clickable-rows"
            empty-text="没有待审核配方"
            highlight-current-row
            :row-class-name="rowClass"
            @row-click="(row: RecipeListItemDto) => select(row.id)"
          >
            <el-table-column prop="code" label="编码" width="110" fixed />
            <el-table-column prop="name" label="名称" min-width="120" />
            <el-table-column label="待审节点" width="100">
              <template #default="{ row }">{{ levelLabel(row.pendingLevel) }}</template>
            </el-table-column>
            <el-table-column label="审版" width="56">
              <template #default="{ row }">{{ row.reviewVersion ? `v${row.reviewVersion}` : "—" }}</template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
      <el-col :span="16" :xs="24">
        <el-empty v-if="!detail && !detailLoading" description="选择左侧配方，在此审阅 Procedure 与冻结 Setpoints 矩阵" />
        <template v-else-if="detail">
          <el-card v-loading="detailLoading">
            <template #header>
              <div class="head">
                <div>
                  <b>{{ detail.code }} {{ detail.name }}</b>
                  <div class="muted">产品 {{ detail.productName }} · 审核版 v{{ review?.versionNumber ?? "—" }} · 状态 {{ recipeStatusLabel(review?.status) }}</div>
                </div>
                <div>
                  <el-button v-if="canDecide" type="success" @click="decide('Approved')">通过并电子签名</el-button>
                  <el-button v-if="canDecide" type="danger" @click="decide('Rejected')">驳回</el-button>
                  <el-button @click="$router.push(`/recipes/${detail.id}`)">打开设计器</el-button>
                </div>
              </div>
            </template>
            <el-alert class="gap-after"
              v-if="pending"
              :closable="false"
              show-icon
              :type="canDecide ? 'warning' : 'info'"
              :title="`当前节点：${levelLabel(pending.level)}`"
              :description="pending.meaning || pendingMeaningFallback"
             
            />
            <el-alert class="gap-after"
              v-else
              :closable="false"
              type="success"
              title="本版本已无待审节点"
             
            />
            <p v-if="review?.changeNote" class="muted">变更说明：{{ review.changeNote }}</p>
            <div v-if="diff" class="diff-block">
              <h4>相对生效版 v{{ diff.fromVersion }} 的差异</h4>
              <p v-if="diff.addedSteps.length">新增工步：{{ diff.addedSteps.join("、") }}</p>
              <p v-if="diff.removedSteps.length">删除工步：{{ diff.removedSteps.join("、") }}</p>
              <el-table v-if="diff.changes.length" :data="diff.changes" size="small" max-height="220" border>
                <el-table-column prop="path" label="路径" min-width="180" fixed />
                <el-table-column prop="before" label="生效版" />
                <el-table-column prop="after" label="审核版" />
              </el-table>
              <el-empty
                v-else-if="!diff.addedSteps.length && !diff.removedSteps.length"
                description="与生效版工艺内容相同"
              />
            </div>
            <h4>Procedure / Steps</h4>
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
            <h4>Parameters / Setpoints 矩阵（只读审阅）</h4>
            <SetpointMatrix
              v-if="review"
              :steps="review.steps"
              :selected-id="selectedStepId"
              :changed-keys="matrixChanged"
              readonly
              :max-height="280"
              @select="selectedStepId = $event"
            />
            <h4><HelpTip term="电子签名">电子签名链</HelpTip></h4>
            <el-table :data="review?.approvals ?? []" size="small" border>
              <el-table-column label="级别" width="140" fixed>
                <template #default="{ row }">{{ levelLabel(row.level) }}</template>
              </el-table-column>
              <el-table-column prop="decision" label="结论" width="120">
                <template #default="{ row }">
                  <el-tag size="small" :type="approvalDecisionTagType(row.decision)" effect="dark">{{ approvalDecisionLabel(row.decision) }}</el-tag>
                </template>
              </el-table-column>
              <el-table-column prop="reviewerName" label="签署人" />
              <el-table-column prop="meaning" label="签署含义" min-width="240" />
              <el-table-column prop="comment" label="意见" />
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
import { ElMessage, ElMessageBox } from "element-plus";
import http from "../../api/http";
import { esignPassword } from "../../utils/esign";
import type { ApprovalDto, RecipeDetailDto, RecipeListItemDto, RecipeVersionDiffDto, RecipeVersionDto } from "../../api/types";
import { useAuthStore } from "../../stores/auth";
import ProcedureFlow from "../../components/ProcedureFlow.vue";
import SetpointMatrix from "../../components/SetpointMatrix.vue";
import { changedMatrixKeys } from "../../setpointMatrix";
import {
  approvalDecisionLabel,
  approvalDecisionTagType,
  approvalLevelLabel as levelLabel,
  recipeStatusLabel
} from "../../utils/labels";
import { useLoad } from "../../utils/useLoad";
import HelpTip from "../../components/HelpTip.vue";

const route = useRoute();
const auth = useAuthStore();
const all = ref<RecipeListItemDto[]>([]);
const items = computed(() => all.value.filter((r) => r.draftStatus === "InReview"));
const detail = ref<RecipeDetailDto | null>(null);
const selectedId = ref<string | null>(null);
const selectedStepId = ref<string | null>(null);
const diff = ref<RecipeVersionDiffDto | null>(null);
const { loading, error, run } = useLoad();
const detailLoading = ref(false);

const review = computed<RecipeVersionDto | null>(() => {
  const versions = detail.value?.versions ?? [];
  return versions.find((v) => v.status === "InReview")
    ?? detail.value?.draft
    ?? null;
});

const pending = computed<ApprovalDto | undefined>(() =>
  review.value?.approvals.find((a) => a.decision === "Pending"));

const pendingMeaningFallback = "请再次输入登录密码作为电子签名。";

const canDecide = computed(() => {
  const node = pending.value;
  if (!node || review.value?.status !== "InReview") return false;
  const role = auth.user?.role;
  return role === "Admin"
    || (node.level === "Supervisor" && role === "Supervisor")
    || (node.level === "Quality" && role === "Quality");
});

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
  selectedId.value = id;
  detailLoading.value = true;
  try {
    detail.value = (await http.get<RecipeDetailDto>(`/recipes/${id}`)).data;
    selectedStepId.value = review.value?.steps[0]?.id ?? null;
    diff.value = null;
    const approved = detail.value.approved?.versionNumber;
    const reviewing = review.value?.versionNumber;
    if (approved && reviewing && approved !== reviewing) {
      try {
        diff.value = (await http.get<RecipeVersionDiffDto>(`/recipes/${id}/compare`, {
          params: { fromVersion: approved, toVersion: reviewing }
        })).data;
      } catch {
        diff.value = null;
      }
    }
  } catch (e) {
    // 原先详情取数失败毫无反馈，右侧区域会一直停在"请选择左侧配方"。
    ElMessage.error(`配方详情加载失败：${(e as Error).message}`);
    detail.value = null;
    diff.value = null;
  } finally {
    detailLoading.value = false;
  }
}

async function decide(decision: "Approved" | "Rejected") {
  if (!detail.value) return;
  try {
    const meaning = pending.value?.meaning ?? pendingMeaningFallback;
    const { value: comment } = await ElMessageBox.prompt(
      `${meaning}\n\n${decision === "Approved" ? "审核意见（通过）" : "驳回原因"}`,
      `当前节点：${levelLabel(pending.value?.level)}`
    );
    const password = await esignPassword(`当前节点：${levelLabel(pending.value?.level)}`);
    await http.post(`/recipes/${detail.value.id}/decide`, { decision, comment, password });
    ElMessage.success(decision === "Approved" ? "已电子签名通过" : "已驳回");
    await loadList();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  }
}

onMounted(loadList);
</script>

<style scoped>
.head { display: flex; justify-content: space-between; gap: var(--space-3); align-items: flex-start; flex-wrap: wrap; }
.muted { color: var(--muted); font-size: 12px; margin: 0 0 var(--space-2); }
h4 { margin: var(--space-4) 0 var(--space-2); font-size: 14px; }
.diff-block { margin: var(--space-2) 0 var(--space-4); }
.diff-block p { color: var(--text-body); font-size: 13px; margin: 0 0 var(--space-2); }
</style>
