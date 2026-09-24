<template>
  <section class="ebr-block">
    <h3>{{ title }}</h3>
    <p v-if="hint" class="ebr-hint">{{ hint }}</p>
    <slot />
  </section>
</template>

<script setup lang="ts">
/**
 * 电子批记录的一个归档区块。
 *
 * 为什么单独一个组件：11 个区块的标题层级、间距和"标题 + 一行说明"结构必须一模一样，
 * 归档件的排版是审查时读的东西，不该靠每个子组件各自记。
 * 标题与提示由父页传**已翻译**的字符串：slot 内容属于父作用域，子组件的 scoped 样式管不到它。
 */
defineProps<{ title: string; hint?: string }>();
</script>

<style scoped>
.ebr-block { margin-bottom: var(--space-5); }
.ebr-block h3 { margin: 0 0 var(--space-2); font-size: 16px; }
.ebr-hint { color: var(--muted); font-size: 12px; margin: 0 0 var(--space-2); }
</style>
