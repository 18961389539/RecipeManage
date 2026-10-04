<template>
  <div class="chain-admin">
    <div class="page-title">
      <div>
        <h2>{{ $t("审批链配置") }}<PageGuideButton guide-key="approvalChains" /></h2>
        <span>
          {{ $t("一条链 = 一串有序的「谁来签 · 签的时候看到哪句话」。配方可以各走各的链，没选的走默认链。") }}
          <b>{{ $t("改链只影响之后的提交") }}</b>{{ $t("：在审版本的节点在提交那刻已经冻结，改这里动不了它。") }}
        </span>
      </div>
      <div>
        <el-button :loading="loading" @click="load">{{ $t("刷新") }}</el-button>
        <el-button type="primary" @click="create">{{ $t("新建链") }}</el-button>
      </div>
    </div>
    <el-alert v-if="error" class="gap-after" :closable="false" type="error" show-icon
      :title="$t('审批链加载失败：{0}', [error])"
    />

    <el-row class="chain-layout" :gutter="12">
      <el-col class="chain-list-col" :span="24" :lg="9">
        <el-card class="chain-list-card" :header="$t('已有的链')">
          <p v-if="isMobile" class="mobile-table-hint">{{ $t("窄屏下左右滑动表格查看其余列和行操作。") }}</p>
          <el-table :data="chains" v-loading="loading" highlight-current-row :row-class-name="rowClass"
            class="clickable-rows" :empty-text="$t('还没有审批链')" @row-click="select">
            <el-table-column prop="code" :label="$t('编码')" width="110" fixed />
            <el-table-column :label="$t('链')" min-width="190">
              <template #default="{ row }">
                <button class="chain-select" type="button" @click.stop="select(row)">
                  <span class="chain-name">{{ row.name }}</span>
                  <span v-if="row.steps.length" class="chain-flow">
                    <template v-for="(step, index) in row.steps" :key="index">
                      <span class="chain-flow-step">
                        <span class="chain-flow-index">{{ index + 1 }}</span>
                        <span class="chain-flow-copy">
                          <b>{{ userRoleLabel(step.requiredRole) }}</b>
                          <small v-if="step.title && step.title !== userRoleLabel(step.requiredRole)">{{ step.title }}</small>
                        </span>
                      </span>
                      <span v-if="index < row.steps.length - 1" class="chain-flow-arrow" aria-hidden="true">→</span>
                    </template>
                  </span>
                  <span v-else class="chain-flow-empty muted">{{ $t("（没有节点）") }}</span>
                </button>
              </template>
            </el-table-column>
            <el-table-column :label="$t('状态')" width="126">
              <template #default="{ row }">
                <div class="chain-status">
                  <el-tag v-if="row.isDefault" size="small" type="success" effect="dark">{{ $t("默认") }}</el-tag>
                  <el-tag size="small" :type="row.enabled ? 'success' : 'info'" effect="plain">
                    {{ row.enabled ? $t("已启用") : $t("停用") }}
                  </el-tag>
                </div>
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>

      <el-col class="chain-editor-col" :span="24" :lg="15">
        <div v-if="!draft.id && !draft.steps.length" class="chain-empty">
          <div class="chain-empty-flow" aria-hidden="true"><i>1</i><b></b><i>2</i><b></b><i>3</i></div>
          <p>{{ $t("选择左侧一条链，或点「新建链」") }}</p>
        </div>
        <el-card v-else class="chain-editor-card">
          <template #header>
            <div class="head">
              <b>{{ draft.id ? $t("编辑 {0}", [draft.code]) : $t("新建审批链") }}</b>
              <div>
                <!-- 有未保存改动才出现：把右栏改了一半又点「新建链」/切别的链，
                     以前是静默覆盖，现在先给一次明确的回退入口。 -->
                <el-button v-if="dirty" @click="discard">{{ $t("放弃修改") }}</el-button>
                <HelpTip term="电子签名" plain placement="bottom">
                  <el-button type="primary" :loading="saving" :disabled="saving || !dirty" @click="save">{{ $t("保存并电子签名") }}</el-button>
                </HelpTip>
                <el-button v-if="draft.id" type="danger" plain :loading="saving" @click="remove">{{ $t("删除") }}</el-button>
              </div>
            </div>
          </template>
          <el-form class="chain-meta" label-width="88px" :disabled="saving">
            <el-form-item :label="$t('编码')"><el-input v-model="draft.code" :disabled="!!draft.id" :placeholder="$t('如 short-qa')" /></el-form-item>
            <el-form-item :label="$t('名称')"><el-input v-model="draft.name" :placeholder="$t('如 小变更短链')" /></el-form-item>
            <el-form-item>
              <template #label><HelpTip term="默认链" /></template>
              <el-switch v-model="draft.isDefault" />
            </el-form-item>
            <el-form-item :label="$t('启用')"><el-switch v-model="draft.enabled" /></el-form-item>
          </el-form>

          <h4 class="steps-heading">
            <span>{{ $t("审核节点（自上而下依次签）") }}</span>
            <span class="steps-count">{{ draft.steps.length }} / {{ approverRoles.length }}</span>
          </h4>
          <p v-if="isMobile" class="mobile-table-hint">{{ $t("窄屏下左右滑动表格查看其余列和行操作。") }}</p>
          <el-table class="steps-table" :data="draft.steps" size="small" border max-height="330">
            <el-table-column label="#" width="42">
              <template #default="{ $index }">{{ $index + 1 }}</template>
            </el-table-column>
            <el-table-column :label="$t('节点名称')" min-width="110">
              <template #default="{ row }"><el-input v-model="row.title" :placeholder="$t('如 工艺主管')" /></template>
            </el-table-column>
            <el-table-column :label="$t('要求角色')" width="118">
              <template #default="{ row }">
                <el-select v-model="row.requiredRole" style="width:100%">
                  <el-option v-for="r in approverRoles" :key="r" :label="userRoleLabel(r)" :value="r" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column :label="$t('通过含义')" min-width="180">
              <template #default="{ row }"><el-input v-model="row.meaningApproved" type="textarea" :autosize="{ minRows: 1, maxRows: 3 }" /></template>
            </el-table-column>
            <el-table-column :label="$t('驳回含义')" min-width="180">
              <template #default="{ row }"><el-input v-model="row.meaningRejected" type="textarea" :autosize="{ minRows: 1, maxRows: 3 }" /></template>
            </el-table-column>
            <el-table-column :label="$t('顺序')" width="92">
              <template #default="{ row, $index }">
                <el-button link :disabled="$index === 0" @click="move($index, -1)">{{ $t("上移") }}</el-button>
                <el-button link :disabled="$index === draft.steps.length - 1" @click="move($index, 1)">{{ $t("下移") }}</el-button>
                <div><el-button link type="danger" @click="dropStep($index)">{{ $t("删除") }}</el-button></div>
              </template>
            </el-table-column>
          </el-table>
          <el-button class="gap-before-sm" size="small" @click="addStep">{{ $t("添加节点") }}</el-button>
          <p class="muted">
            {{ $t("同一角色在一条链里只能出现一次；提交配方的人（工艺工程师）与操作设备的人（操作员）不能当审核角色。 签名含义会随整条链冻结进审批记录，事后改这里不会影响已经提交的那一版。") }}
          </p>
        </el-card>
      </el-col>
    </el-row>
  </div>
</template>

<script setup lang="ts">
import { t } from "../../i18n";
import { computed, onMounted, onUnmounted, reactive, ref } from "vue";
import { onBeforeRouteLeave } from "vue-router";
import { ElMessage, ElMessageBox } from "element-plus";
import {
  deleteApprovalChain, listApprovalChains, saveApprovalChain
} from "../../api/approvalChains";
import type { ApprovalChainDto, ApprovalChainStepRequest, UserRole } from "../../api/types";
import { userRoleLabel } from "../../utils/labels";
import { useLoad } from "../../utils/useLoad";
import { useIsMobile } from "../../utils/useMedia";
import { esignPassword } from "../../utils/esign";
import HelpTip from "../../components/HelpTip.vue";

/**
 * 审批链配置台（Admin）。
 *
 * 编辑态用一份本地 draft：节点是一整条链覆盖保存的，没有逐节点的增删接口，
 * 所以列表里点哪条就在右边展开哪条，改完统一签一次。
 */

// 提交配方与操作设备的人不能签自己提交/自己操作的版本，所以不给选。
const approverRoles: UserRole[] = ["Supervisor", "Quality", "Admin"];
const isMobile = useIsMobile();

const chains = ref<ApprovalChainDto[]>([]);
const draft = reactive<{
  id: string | null; code: string; name: string; isDefault: boolean; enabled: boolean;
  steps: ApprovalChainStepRequest[];
}>({ id: null, code: "", name: "", isDefault: false, enabled: true, steps: [] });
const selectedId = ref<string | null>(null);
const saving = ref(false);
const { loading, error, runValue } = useLoad();
/**
 * 最近一次载入/保存时的草稿指纹：右栏改动只有「保存并电子签名」一条落地出口，
 * 切链、点「新建链」都会整份覆盖草稿。以前是静默丢弃，现在覆盖前先问一句，
 * 并给一个显式的「放弃修改」回退入口。
 */
const baseline = ref("");
function draftJson(): string {
  return JSON.stringify({
    id: draft.id, code: draft.code, name: draft.name,
    isDefault: draft.isDefault, enabled: draft.enabled,
    steps: draft.steps.map((s) => ({ ...s }))
  });
}
const dirty = computed(() =>
  (draft.id !== null || draft.steps.length > 0) && draftJson() !== baseline.value);

function rowClass({ row }: { row: ApprovalChainDto }) {
  return row.id === selectedId.value ? "current-row" : "";
}

async function load() {
  await runValue(listApprovalChains(), rows => {
    chains.value = rows;
    // 选中项没了（被删掉或首次进来）就把右栏收回空态，避免继续编辑一条不存在的链。
    if (!rows.some(c => c.id === selectedId.value)) {
      selectedId.value = null;
      resetDraft();
    }
  });
}

function resetDraft() {
  Object.assign(draft, { id: null, code: "", name: "", isDefault: false, enabled: true, steps: [] });
  baseline.value = "";
}

/** 覆盖前问一句：有未保存改动时返回 false（用户取消）。无改动直接放行。 */
async function confirmDiscard(): Promise<boolean> {
  if (!dirty.value) return true;
  try {
    await ElMessageBox.confirm(
      t("当前草稿有未保存的修改，继续将丢失这些修改。"),
      t("放弃未保存的修改"),
      { type: "warning", confirmButtonText: t("放弃修改"), cancelButtonText: t("取消") }
    );
    return true;
  } catch {
    return false;
  }
}

/** 把某条链装载进草稿并重置指纹。 */
function applyChain(chain: ApprovalChainDto) {
  selectedId.value = chain.id;
  Object.assign(draft, {
    id: chain.id, code: chain.code, name: chain.name,
    isDefault: chain.isDefault, enabled: chain.enabled,
    steps: chain.steps.map(s => ({
      title: s.title, requiredRole: s.requiredRole,
      meaningApproved: s.meaningApproved, meaningRejected: s.meaningRejected
    }))
  });
  baseline.value = draftJson();
}

async function create() {
  if (!(await confirmDiscard())) return;
  selectedId.value = null;
  resetDraft();
  Object.assign(draft, { code: "", name: "", steps: [newStep()] });
  baseline.value = draftJson();
}

async function select(chain: ApprovalChainDto) {
  // 点已选中的链且无改动是空操作；有改动时（含误点本条）先确认，别把编辑内容换回库里版本。
  if (chain.id === selectedId.value && !dirty.value) return;
  if (!(await confirmDiscard())) return;
  applyChain(chain);
}

/** 显式放弃：新建态清空回空态；已有链回退到库里那一版（按钮本身就是确认，不再二次弹窗）。 */
function discard() {
  const current = chains.value.find(c => c.id === selectedId.value);
  if (current) applyChain(current);
  else resetDraft();
}

function newStep(): ApprovalChainStepRequest {
  const role = approverRoles.find(r => !draft.steps.some(s => s.requiredRole === r)) ?? "Supervisor";
  return { title: "", requiredRole: role, meaningApproved: "", meaningRejected: "" };
}

function addStep() {
  draft.steps.push(newStep());
}

function dropStep(index: number) {
  draft.steps.splice(index, 1);
}

function move(index: number, delta: number) {
  const to = index + delta;
  if (to < 0 || to >= draft.steps.length) return;
  const [step] = draft.steps.splice(index, 1);
  draft.steps.splice(to, 0, step);
}

async function save() {
  if (saving.value) return;
  saving.value = true;
  try {
    const password = await esignPassword(t("保存审批链"),
      t("确认把「{0}」保存为 {1} 级审批链。", draft.name || draft.code, draft.steps.length));
    const saved = await saveApprovalChain({
      id: draft.id, code: draft.code.trim(), name: draft.name.trim(),
      isDefault: draft.isDefault, enabled: draft.enabled, steps: draft.steps, password
    });
    ElMessage.success(t("审批链已保存"));
    await load();
    const hit = chains.value.find(c => c.id === saved.id);
    // 保存成功后草稿即库里版本：先把指纹对齐，后面的 select 才不会把它当成未保存改动而弹确认。
    baseline.value = draftJson();
    if (hit) void select(hit);
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    saving.value = false;
  }
}

async function remove() {
  if (!draft.id || saving.value) return;
  try {
    await ElMessageBox.confirm(
      t("删除后，仍选用 {0} 的配方必须改链才能提交。在审版本不受影响。", draft.code),
      t("删除审批链 {0}", draft.name),
      { type: "warning", confirmButtonText: t("删除"), cancelButtonText: t("取消") }
    );
  } catch {
    return;
  }
  saving.value = true;
  try {
    const password = await esignPassword(t("删除审批链"), t("确认删除「{0}」。", draft.name));
    await deleteApprovalChain(draft.id, password);
    ElMessage.success(t("审批链已删除"));
    resetDraft();
    await load();
  } catch (e) {
    if ((e as string) !== "cancel") ElMessage.error((e as Error).message ?? String(e));
  } finally {
    saving.value = false;
  }
}

onMounted(load);

/**
 * 离开这一页也要问：右栏可以改到一半直接点侧栏换页，浏览器刷新 / 关标签页同理。
 * 与配方设计器同一套守卫写法（那边有 900 行草稿，这边虽然只有一条链，但丢的是同样的手输内容）。
 */
function onBeforeUnload(e: BeforeUnloadEvent) {
  if (!dirty.value) return;
  e.preventDefault();
  e.returnValue = "";
}

onMounted(() => window.addEventListener("beforeunload", onBeforeUnload));
onUnmounted(() => window.removeEventListener("beforeunload", onBeforeUnload));

onBeforeRouteLeave(async () => {
  if (!dirty.value) return true;
  return await confirmDiscard();
});
</script>

<style scoped>
.muted { color: var(--muted); font-size: 12px; }
.chain-name { font-weight: 600; }
.head { display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); flex-wrap: wrap; }
h4 { margin: var(--space-4) 0 var(--space-2); font-size: 14px; }
.chain-layout { row-gap: var(--space-3); }
.chain-list-card,
.chain-editor-card { height: 100%; }
.chain-select {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: var(--space-2);
  width: 100%;
  padding: var(--space-1) 0;
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}
.chain-flow { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-1); }
.chain-flow-step {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  max-width: 100%;
  padding: 3px 6px;
  border: 1px solid var(--line);
  border-radius: 4px;
  background: var(--sunken);
}
.chain-flow-index {
  display: grid;
  flex: none;
  place-items: center;
  width: 18px;
  height: 18px;
  border-radius: 50%;
  background: var(--tint);
  color: var(--accent-bright);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}
.chain-flow-copy { display: grid; min-width: 0; gap: 1px; }
.chain-flow-copy b { font-size: 12px; font-weight: 600; }
.chain-flow-copy small { color: var(--muted); font-size: 11px; }
.chain-flow-arrow { color: var(--muted); font-size: 12px; }
.chain-flow-empty { padding: 2px 0; }
.chain-status { display: flex; flex-wrap: wrap; gap: var(--space-1); }
.chain-empty {
  display: flex;
  min-height: 220px;
  height: 100%;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  border: 1px dashed var(--line);
  border-radius: 6px;
  background: var(--sunken);
}
.chain-empty-flow { display: flex; align-items: center; gap: 6px; }
.chain-empty-flow i {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border: 1px solid var(--line);
  border-radius: 50%;
  color: var(--accent-bright);
  font-size: 11px;
  font-style: normal;
}
.chain-empty-flow b { width: 22px; height: 1px; background: var(--line); }
.chain-empty p { margin: var(--space-3) 0 0; color: var(--muted); font-size: 13px; }
.chain-meta {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  column-gap: var(--space-4);
}
.chain-meta :deep(.el-form-item) { margin-bottom: var(--space-3); }
.steps-heading { display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); }
.steps-count {
  color: var(--muted);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
  font-weight: 500;
}
@media (max-width: 600px) {
  .chain-meta { grid-template-columns: minmax(0, 1fr); }
  .chain-empty { min-height: 150px; }
}
</style>
