<template>
  <!-- 解释文案来自 utils/glossary.ts。术语未收录且无快捷键时降级为普通文本，避免出现无内容的空提示。 -->
  <el-tooltip
    v-if="text"
    :content="text"
    :placement="placement"
    :show-after="showAfter"
    effect="dark"
    popper-class="help-tip-pop"
  >
    <span :class="plain ? (block ? 'help-tip-plain is-block' : 'help-tip-plain') : 'help-tip'"><slot>{{ $t(term) }}</slot></span>
  </el-tooltip>
  <span v-else><slot>{{ term }}</slot></span>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { tipOf } from "../utils/glossary";
import { chordHint } from "../shortcuts/chords";

const props = withDefaults(
  defineProps<{
    /** glossary 的 key，建议使用界面上实际渲染的中文词 */
    term?: string;
    /** 同时展示按键；与 glossary 正文拼成多段 Tooltip */
    chord?: string;
    /** Ctrl 组合可在输入框触发时，Tooltip 里写明「输入框里也可按」 */
    allowInInput?: boolean;
    /** 额外一段（如「当前状态不可用」），仍建议取自 glossary */
    extra?: string;
    /** 包按钮/搜索框时不要虚线下划线，并让禁用按钮也能悬停 */
    plain?: boolean;
    /** plain 时拉满父级宽度（帮助列表每一行） */
    block?: boolean;
    /** 建议显示方向；表格表头一般用 top，顶栏动作可用 bottom */
    placement?: "top" | "top-start" | "top-end" | "right" | "bottom" | "bottom-start" | "bottom-end" | "left";
    showAfter?: number;
  }>(),
  { term: "", chord: "", extra: "", plain: false, block: false, placement: "top", showAfter: 120, allowInInput: false }
);

const text = computed(() => {
  const parts: string[] = [];
  const body = tipOf(props.term);
  if (body) parts.push(body);
  const extra = props.extra.trim();
  if (extra) parts.push(extra);
  if (props.chord) parts.push(chordHint(props.chord, props.allowInInput));
  return parts.length ? parts.join("\n\n") : undefined;
});
</script>

<style scoped>
.help-tip {
  cursor: help;
  border-bottom: 1px dashed currentColor;
  /* 虚线下划线用于示意"可悬停查看含义"，不参与选中 */
  text-decoration: none;
}
.help-tip-plain {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  max-width: 100%;
  vertical-align: middle;
}
.help-tip-plain :deep(.el-button.is-disabled) {
  pointer-events: none;
}
.help-tip-plain.is-block {
  display: flex;
  width: 100%;
  flex-shrink: 1;
}
</style>
