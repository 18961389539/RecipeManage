<template>

  <div>
    <p class="muted">{{ $t("写 PLC 的相模板按设备类集中维护；程序号给设计器和握手 Step_Type 用。等待 / 质检 / 人工确认不进本库。") }}</p>
    <el-collapse v-if="classes.length" v-model="openClassIds">
      <el-collapse-item
        v-for="cls in classes"
        :key="cls.id"
        :name="cls.id"
        :class="{ 'focus-class': isFocus(cls) }"
      >
        <template #title>
          <b>{{ cls.code }}</b>
          <span class="muted"> · {{ cls.name }} · {{ cls.templates.length }} 条</span>
          <span v-if="isFocus(cls)" class="focus-mark">{{ $t("当前类") }}</span>
        </template>
        <p v-if="cls.description" class="muted">{{ cls.description }}</p>
        <el-table :data="cls.templates" size="small" :empty-text="$t('该类还没有相模板')">
          <el-table-column prop="code" :label="$t('编码')" width="140" />
          <el-table-column prop="name" :label="$t('名称')" min-width="120" />
          <el-table-column :label="$t('执行类')" width="88">
            <template #default="{ row }">{{ executionKindLabel(executionKind(row.stepType)) }}</template>
          </el-table-column>
          <el-table-column :label="$t('程序号')" width="88">
            <template #header><HelpTip term="程序号" /></template>
            <template #default="{ row }">{{ templateProgram(row) }}</template>
          </el-table-column>
          <!-- 这列是 ISA-88 的 Operation（工艺操作），不是"操作按钮"。
               两列此前都叫「操作」，同一张表里两个同名列分不清哪个是数据哪个是动作。 -->
          <el-table-column prop="operation" min-width="140">
            <template #header><HelpTip term="Operation">{{ $t("工艺操作") }}</HelpTip></template>
          </el-table-column>
          <el-table-column prop="watchdogSeconds" :label="$t('看门狗')" width="80" />
          <el-table-column :label="$t('参数槽')" width="72">
            <template #default="{ row }">{{ row.parameters.length }}</template>
          </el-table-column>
          <el-table-column v-if="canEdit" :label="$t('操作')" width="140" fixed="right">
            <template #default="{ row }">
              <el-button link type="primary" @click="open(cls, row)">{{ $t("编辑") }}</el-button>
              <el-button link type="danger" :loading="busy === row.id" @click="remove(cls, row)">{{ $t("删除") }}</el-button>
            </template>
          </el-table-column>
        </el-table>
        <el-button v-if="canEdit" class="gap-before-sm" size="small" type="primary" plain @click="open(cls)">
          {{ $t("新增相模板") }}
        </el-button>
      </el-collapse-item>
    </el-collapse>
    <p v-else class="none-note">{{ $t("没有可维护的设备类：先建设备类，才能给它加相模板。") }}</p>

    <PhaseTemplateDialog v-model="dialogVisible" :cls="dialogCls" :template="dialogTemplate" :can-edit="canEdit" @saved="emit('changed')" />
  </div>
</template>

<script setup lang="ts">
import { t } from "../i18n";

import { ref, watch } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import { deletePhaseTemplate } from "../api/equipment";
import type { EquipmentClassDto, PhaseTemplateDto } from "../api/types";
import { executionKindLabel } from "../utils/labels";
import { executionKind } from "../utils/plcProgram";
import { templateProgram } from "../utils/phaseTemplate";
import HelpTip from "./HelpTip.vue";
import PhaseTemplateDialog from "./PhaseTemplateDialog.vue";

/**
 * 相库面板：按设备类折叠展示相模板，并负责模板的新增/编辑/删除。
 *
 * 展开态归这里管（搜索时全开、URL 指定类时只开那个、否则首次全开一次），
 * 因为它只服务于这块折叠列表，放到页面里只会让页面再多三个状态。
 */
const props = withDefaults(defineProps<{
  classes: EquipmentClassDto[];
  canEdit: boolean;
  focusClassId?: string;
  expandAll?: boolean;
}>(), { focusClassId: "", expandAll: false });
const emit = defineEmits<{ changed: [] }>();

const openClassIds = ref<string[]>([]);
const primed = ref(false);
const busy = ref("");
const dialogVisible = ref(false);
const dialogCls = ref<EquipmentClassDto | null>(null);
const dialogTemplate = ref<PhaseTemplateDto | null>(null);

function isFocus(cls: EquipmentClassDto) {
  return !!props.focusClassId && cls.id === props.focusClassId;
}

watch(
  () => [props.classes.map((c) => c.id).join(), props.expandAll, props.focusClassId] as const,
  () => {
    const list = props.classes.map((c) => c.id);
    if (!list.length) return;
    if (props.expandAll) {
      // 搜索命中时全开：搜到某个相模板却还要再点一下设备类才能看见它，等于没搜。
      openClassIds.value = list;
      primed.value = true;
      return;
    }
    if (props.focusClassId) {
      openClassIds.value = list.filter((id) => id === props.focusClassId);
      primed.value = true;
      return;
    }
    if (!primed.value) {
      openClassIds.value = list;
      primed.value = true;
    }
  },
  { immediate: true }
);

function open(cls: EquipmentClassDto, row?: PhaseTemplateDto) {
  dialogCls.value = cls;
  dialogTemplate.value = row ?? null;
  dialogVisible.value = true;
}

async function remove(cls: EquipmentClassDto, row: PhaseTemplateDto) {
  try {
    await ElMessageBox.confirm(
      t("删除 {0} {1} 只影响新编排，已保存工步仍带原程序号。", row.code, row.name),
      t("删除相模板"),
      { type: "warning", confirmButtonText: t("删除"), cancelButtonText: t("取消") }
    );
  } catch {
    return;
  }
  busy.value = row.id;
  try {
    await deletePhaseTemplate(cls.id, row.id);
    ElMessage.success(t("已删除相模板"));
    emit("changed");
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    busy.value = "";
  }
}
</script>

<style scoped>
.muted { color: var(--muted); font-size: 12px; }
.focus-mark { margin-left: 8px; color: var(--accent-bright); font-size: 12px; }
:deep(.focus-class > .el-collapse-item__header) { background: var(--tint); }
</style>
