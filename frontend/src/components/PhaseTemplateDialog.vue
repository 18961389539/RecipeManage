<template>
  <el-dialog v-model="visible" :title="form.id ? $t('编辑相模板') : $t('新增相模板')" width="900px">
    <p class="muted">{{ cls?.code }} · {{ cls?.name }}。编码全局唯一；同一设备类内程序号不能重复。</p>
    <el-form label-width="108px" :disabled="!canEdit">
      <el-form-item :label="$t('编码')"><el-input v-model="form.code" :disabled="!!form.id" :placeholder="$t('如 PH-SPRAY')" /></el-form-item>
      <el-form-item :label="$t('名称')"><el-input v-model="form.name" /></el-form-item>
      <el-form-item>
        <template #label><HelpTip term="类型别名">{{ $t("类型别名") }}</HelpTip></template>
        <el-select v-model="form.stepType" style="width:100%" @change="onType">
          <el-option v-for="t in PROCESS_TYPES" :key="t" :label="`${stepTypeLabel(t)}（缺省程序 ${DEFAULT_PROGRAM[t]}）`" :value="t" />
        </el-select>
      </el-form-item>
      <el-form-item>
        <template #label><HelpTip term="程序号">{{ $t("程序号") }}</HelpTip></template>
        <el-input-number v-model="form.plcProgramId" :min="1" :max="99" />
      </el-form-item>
      <el-form-item :label="$t('操作')"><el-input v-model="form.operation" :placeholder="$t('如 OP-Rinse 水冲洗')" /></el-form-item>
      <el-form-item :label="$t('看门狗（秒）')"><el-input-number v-model="form.watchdogSeconds" :min="5" /></el-form-item>
    </el-form>
    <el-table :data="form.parameters" size="small" max-height="240">
      <el-table-column prop="slotIndex" :label="$t('槽')" width="48" />
      <el-table-column :label="$t('参数')" min-width="90">
        <template #default="{ row }"><el-input v-model="row.name" /></template>
      </el-table-column>
      <el-table-column :label="$t('设定值')" width="88">
        <template #default="{ row }"><el-input-number v-model="row.setpoint" :controls="false" /></template>
      </el-table-column>
      <el-table-column :label="$t('下限')" width="72">
        <template #default="{ row }"><el-input-number v-model="row.min" :controls="false" /></template>
      </el-table-column>
      <el-table-column :label="$t('上限')" width="72">
        <template #default="{ row }"><el-input-number v-model="row.max" :controls="false" /></template>
      </el-table-column>
      <el-table-column :label="$t('单位')" width="64">
        <template #default="{ row }"><el-input v-model="row.engineeringUnit" /></template>
      </el-table-column>
      <el-table-column width="64">
        <template #header><HelpTip term="写PLC" /></template>
        <template #default="{ row }"><el-switch v-model="row.writeToPlc" /></template>
      </el-table-column>
      <el-table-column width="56">
        <template #header><HelpTip term="质检" /></template>
        <template #default="{ row }"><el-switch v-model="row.archiveAsQuality" /></template>
      </el-table-column>
      <el-table-column width="80">
        <template #header><HelpTip term="随批缩放" /></template>
        <template #default="{ row }"><el-switch v-model="row.scaleWithBatch" /></template>
      </el-table-column>
      <el-table-column width="112">
        <template #header><HelpTip term="语义" /></template>
        <template #default="{ row }">
          <el-select v-model="row.semantic" size="small">
            <el-option v-for="s in parameterSemanticOptions" :key="s" :label="parameterSemanticLabel(s)" :value="s" />
          </el-select>
        </template>
      </el-table-column>
      <el-table-column :label="$t('实测点')" min-width="120">
        <template #header><HelpTip term="实测点" /></template>
        <template #default="{ row }">
          <el-input v-model="row.measuredTag" :disabled="!row.archiveAsQuality" :placeholder="$t('留空按名称推断')" />
        </template>
      </el-table-column>
    </el-table>
    <el-button class="gap-before-sm" size="small" @click="addParam">{{ $t("新增参数槽") }}</el-button>
    <el-button class="gap-before-sm" size="small" @click="removeParam">{{ $t("删除末槽") }}</el-button>
    <template #footer>
      <el-button @click="visible = false">{{ $t("关闭") }}</el-button>
      <el-button v-if="canEdit" type="primary" :loading="saving" @click="save">{{ $t("保存") }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { reactive, ref, watch } from "vue";
import { ElMessage } from "element-plus";
import { savePhaseTemplate } from "../api/equipment";
import type { EquipmentClassDto, PhaseParameterDto, PhaseTemplateDto, StepType } from "../api/types";
import { parameterSemanticLabel, parameterSemanticOptions, stepTypeLabel } from "../utils/labels";
import { DEFAULT_PROGRAM, PROCESS_TYPES, defaultTemplateParams, programOutOfRange, templateProgram } from "../utils/phaseTemplate";
import HelpTip from "./HelpTip.vue";

/** 相模板编辑对话框。参数槽的增删与"改类型即换缺省槽"的规则都关在这里。 */
const visible = defineModel<boolean>({ required: true });
const props = withDefaults(defineProps<{
  cls?: EquipmentClassDto | null;
  template?: PhaseTemplateDto | null;
  canEdit?: boolean;
}>(), { cls: null, template: null, canEdit: false });
const emit = defineEmits<{ saved: [] }>();

const saving = ref(false);
const form = reactive({
  id: "",
  code: "",
  name: "",
  stepType: "Heat" as StepType,
  plcProgramId: 1,
  watchdogSeconds: 60,
  operation: "",
  parameters: [] as PhaseParameterDto[]
});

watch(visible, (open) => {
  if (!open) return;
  const row = props.template;
  if (row) {
    Object.assign(form, {
      id: row.id, code: row.code, name: row.name, stepType: row.stepType,
      plcProgramId: templateProgram(row), watchdogSeconds: row.watchdogSeconds,
      operation: row.operation, parameters: row.parameters.map((p) => ({ ...p }))
    });
  } else {
    Object.assign(form, {
      id: "", code: "", name: "", stepType: "Heat", plcProgramId: 1,
      watchdogSeconds: 60, operation: "", parameters: defaultTemplateParams("Heat")
    });
  }
});

function onType(type: StepType) {
  if (form.id) return;
  form.plcProgramId = DEFAULT_PROGRAM[type] ?? 1;
  form.parameters = defaultTemplateParams(type);
}

function addParam() {
  const next = form.parameters.reduce((m, p) => Math.max(m, p.slotIndex + 1), 0);
  form.parameters.push({
    slotIndex: next,
    name: `参数${next}`,
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

function removeParam() {
  form.parameters.pop();
}

async function save() {
  if (!props.cls || saving.value) return;
  if (programOutOfRange(form.plcProgramId)) {
    ElMessage.warning("写 PLC 程序号用 1–6 或 9–99");
    return;
  }
  saving.value = true;
  try {
    await savePhaseTemplate(props.cls.id, form.id || null, {
      code: form.code,
      name: form.name,
      stepType: form.stepType,
      plcProgramId: form.plcProgramId,
      watchdogSeconds: form.watchdogSeconds,
      operation: form.operation,
      parameters: form.parameters
    });
    ElMessage.success("相模板已保存");
    visible.value = false;
    emit("saved");
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    saving.value = false;
  }
}
</script>

<style scoped>
.muted { color: var(--muted); font-size: 12px; }
</style>
