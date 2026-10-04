<template>
  <el-dialog v-model="visible" :title="$t('主配方抬头')" width="480px">
    <el-form :model="form" label-width="96px">
      <el-form-item :label="$t('名称')"><el-input v-model="form.name" /></el-form-item>
      <el-form-item :label="$t('产品编码')"><el-input v-model="form.productCode" /></el-form-item>
      <el-form-item :label="$t('产品名称')"><el-input v-model="form.productName" /></el-form-item>
      <el-form-item :label="$t('说明')"><el-input v-model="form.description" type="textarea" :rows="2" /></el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="visible = false">{{ $t("取消") }}</el-button>
      <el-button type="primary" :loading="saving" @click="save">{{ $t("保存抬头") }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { reactive, ref, watch } from "vue";
import { ElMessage } from "element-plus";
import { t } from "../i18n";
import { updateRecipeHeader } from "../api/recipes";
import type { RecipeDetailDto } from "../api/types";

/** 主配方抬头（名称/产品/说明）。不涉及工艺内容，所以不需要电子签名与变更原因。 */
const visible = defineModel<boolean>({ required: true });
const props = defineProps<{ recipe: RecipeDetailDto | null }>();
const emit = defineEmits<{ saved: [] }>();

const saving = ref(false);
const form = reactive({ name: "", productCode: "", productName: "", description: "" });

watch(visible, (open) => {
  if (!open || !props.recipe) return;
  form.name = props.recipe.name;
  form.productCode = props.recipe.productCode;
  form.productName = props.recipe.productName;
  form.description = props.recipe.description ?? "";
});

async function save() {
  if (!props.recipe || saving.value) return;
  saving.value = true;
  try {
    await updateRecipeHeader(props.recipe.id, {
      name: form.name,
      productCode: form.productCode,
      productName: form.productName,
      description: form.description || null
    });
    ElMessage.success(t("已更新主配方抬头"));
    visible.value = false;
    emit("saved");
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    saving.value = false;
  }
}
</script>
