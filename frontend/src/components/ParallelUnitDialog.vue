<template>
  <el-dialog v-model="visible" :title="$t('新增并行单元规程')" width="480px">
    <p class="muted">{{ $t("新单元与现有工步无前驱连线，可绑定不同 PLC 并行四步握手。编排完后用下方「添加连线」汇合到质检工步。") }}</p>
    <el-form label-width="108px">
      <el-form-item :label="$t('单元规程')"><el-input v-model="form.unit" /></el-form-item>
      <el-form-item>
        <template #label><HelpTip term="当前设备类">{{ $t("设备类") }}</HelpTip></template>
        <el-select v-model="form.classId" style="width:100%" @change="syncTemplate">
          <el-option v-for="cls in classes" :key="cls.id" :label="`${cls.code} · ${cls.name}`" :value="cls.id" />
        </el-select>
      </el-form-item>
      <el-form-item :label="$t('首工步相')">
        <el-select v-model="form.templateId" style="width:100%" :disabled="!templates.length">
          <el-option
            v-for="t in templates"
            :key="t.id"
            :label="`${t.code} ${t.name} · 程序 ${templateProgram(t)}`"
            :value="t.id"
          />
        </el-select>
        <p v-if="!templates.length" class="muted">
          {{ $t("当前设备类没有相模板。") }}
          <el-button link type="primary" @click="emit('openLibrary', form.classId)">{{ $t("去相库添加") }}</el-button>
        </p>
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="visible = false">{{ $t("取消") }}</el-button>
      <el-button type="primary" :disabled="!form.templateId" @click="confirm">{{ $t("添加并行工步") }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, reactive, watch } from "vue";
import { ElMessage } from "element-plus";
import type { EquipmentClassDto } from "../api/types";
import { templateProgram } from "../utils/phaseTemplate";
import HelpTip from "./HelpTip.vue";

/**
 * 新增并行单元规程。
 *
 * 对话框只负责"选哪个类、哪个首工步相"，真正把工步塞进草稿、
 * 并把类盖到这条泳道上的是页面（它才有 working 草稿与设备类映射）。
 */
const visible = defineModel<boolean>({ required: true });
const props = withDefaults(defineProps<{
  classes: EquipmentClassDto[];
  defaultClassId?: string;
  suggestedUnit: string;
}>(), { defaultClassId: "" });
const emit = defineEmits<{
  add: [payload: { unit: string; classId: string; templateId: string }];
  openLibrary: [classId: string];
}>();

const form = reactive({ unit: "", classId: "", templateId: "" });
const templates = computed(() =>
  props.classes.find((c) => c.id === form.classId)?.templates ?? []);

function syncTemplate() {
  if (!templates.value.some((t) => t.id === form.templateId))
    form.templateId = templates.value[0]?.id ?? "";
}

watch(visible, (open) => {
  if (!open) return;
  form.unit = props.suggestedUnit;
  form.classId = props.defaultClassId || props.classes[0]?.id || "";
  syncTemplate();
});
watch(() => form.classId, syncTemplate);

function confirm() {
  const unit = form.unit.trim();
  if (!unit) {
    ElMessage.warning("必须填写单元规程名称");
    return;
  }
  if (!form.templateId) {
    ElMessage.warning("请选择首工步相模板");
    return;
  }
  emit("add", { unit, classId: form.classId, templateId: form.templateId });
  visible.value = false;
}
</script>

<style scoped>
.muted { color: var(--muted); font-size: 12px; }
</style>
