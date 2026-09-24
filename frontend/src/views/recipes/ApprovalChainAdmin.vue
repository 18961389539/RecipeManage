<template>
  <div class="chain-admin">
    <div class="page-title">
      <div>
        <h2>{{ $t("审批链配置") }}</h2>
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

    <el-row :gutter="12">
      <el-col :span="9" :xs="24">
        <el-card :header="$t('已有的链')">
          <el-table :data="chains" v-loading="loading" highlight-current-row :row-class-name="rowClass"
            class="clickable-rows" :empty-text="$t('还没有审批链')" @row-click="select">
            <el-table-column prop="code" :label="$t('编码')" width="110" fixed />
            <el-table-column :label="$t('链')" min-width="200">
              <template #default="{ row }">
                <div class="chain-name">{{ row.name }}</div>
                <div class="muted">{{ pathOf(row) }}</div>
              </template>
            </el-table-column>
            <el-table-column :label="$t('状态')" width="92">
              <template #default="{ row }">
                <el-tag v-if="row.isDefault" size="small" type="success" effect="dark">{{ $t("默认") }}</el-tag>
                <el-tag v-else-if="!row.enabled" size="small" type="info" effect="plain">{{ $t("停用") }}</el-tag>
                <span v-else class="muted">—</span>
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>

      <el-col :span="15" :xs="24">
        <el-empty v-if="!draft" :description="$t('选择左侧一条链，或点「新建链」')" />
        <el-card v-else>
          <template #header>
            <div class="head">
              <b>{{ draft.id ? $t("编辑 {0}", [draft.code]) : $t("新建审批链") }}</b>
              <div>
                <HelpTip term="电子签名" plain placement="bottom">
                  <el-button type="primary" :loading="saving" @click="save">{{ $t("保存并电子签名") }}</el-button>
                </HelpTip>
                <el-button v-if="draft.id" type="danger" plain :loading="saving" @click="remove">{{ $t("删除") }}</el-button>
              </div>
            </div>
          </template>
          <el-form label-width="88px" :disabled="saving">
            <el-form-item :label="$t('编码')"><el-input v-model="draft.code" :disabled="!!draft.id" :placeholder="$t('如 short-qa')" /></el-form-item>
            <el-form-item :label="$t('名称')"><el-input v-model="draft.name" :placeholder="$t('如 小变更短链')" /></el-form-item>
            <el-form-item>
              <template #label><HelpTip term="默认链" /></template>
              <el-switch v-model="draft.isDefault" />
            </el-form-item>
            <el-form-item :label="$t('启用')"><el-switch v-model="draft.enabled" /></el-form-item>
          </el-form>

          <h4>{{ $t("审核节点（自上而下依次签）") }}</h4>
          <el-table :data="draft.steps" size="small" border max-height="330">
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
import { computed, onMounted, reactive, ref } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import {
  deleteApprovalChain, listApprovalChains, saveApprovalChain
} from "../../api/approvalChains";
import type { ApprovalChainDto, ApprovalChainStepRequest, UserRole } from "../../api/types";
import { userRoleLabel } from "../../utils/labels";
import { useLoad } from "../../utils/useLoad";
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

const chains = ref<ApprovalChainDto[]>([]);
const draft = reactive<{
  id: string | null; code: string; name: string; isDefault: boolean; enabled: boolean;
  steps: ApprovalChainStepRequest[];
}>({ id: null, code: "", name: "", isDefault: false, enabled: true, steps: [] });
const selectedId = ref<string | null>(null);
const saving = ref(false);
const { loading, error, runValue } = useLoad();

const pathOf = (chain: ApprovalChainDto) =>
  chain.steps.map(s => `${s.title}（${userRoleLabel(s.requiredRole)}）`).join(" → ") || t("（没有节点）");

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
}

function create() {
  selectedId.value = null;
  resetDraft();
  Object.assign(draft, { code: "", name: "", steps: [newStep()] });
}

function select(chain: ApprovalChainDto) {
  selectedId.value = chain.id;
  Object.assign(draft, {
    id: chain.id, code: chain.code, name: chain.name,
    isDefault: chain.isDefault, enabled: chain.enabled,
    steps: chain.steps.map(s => ({
      title: s.title, requiredRole: s.requiredRole,
      meaningApproved: s.meaningApproved, meaningRejected: s.meaningRejected
    }))
  });
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
    if (hit) select(hit);
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
</script>

<style scoped>
.muted { color: var(--muted); font-size: 12px; }
.chain-name { font-weight: 600; }
.head { display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); flex-wrap: wrap; }
h4 { margin: var(--space-4) 0 var(--space-2); font-size: 14px; }
</style>
