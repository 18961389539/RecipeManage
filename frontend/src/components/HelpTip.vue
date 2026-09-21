<template>
  <!-- 解释文案来自 utils/glossary.ts。术语未收录时降级为普通文本，避免出现无内容的空提示。 -->
  <el-tooltip
    v-if="text"
    :content="text"
    :placement="placement"
    :show-after="120"
    effect="dark"
    popper-class="help-tip-pop"
  >
    <span class="help-tip"><slot>{{ term }}</slot></span>
  </el-tooltip>
  <span v-else><slot>{{ term }}</slot></span>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { tipOf } from "../utils/glossary";

const props = withDefaults(
  defineProps<{
    /** glossary 的 key，建议使用界面上实际渲染的中文词 */
    term?: string;
    /** 建议显示方向；表格表头一般用 top，首列可用 right */
    placement?: "top" | "top-start" | "top-end" | "right" | "bottom" | "bottom-start" | "bottom-end" | "left";
  }>(),
  { term: "", placement: "top" }
);

const text = computed(() => tipOf(props.term));
</script>

<style scoped>
.help-tip {
  cursor: help;
  border-bottom: 1px dashed currentColor;
  /* 虚线下划线用于示意"可悬停查看含义"，不参与选中 */
  text-decoration: none;
}
</style>
