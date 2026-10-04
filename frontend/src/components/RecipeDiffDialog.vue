<template>
  <el-dialog v-model="visible" :title="$t('版本差异')" width="720px">
    <div v-if="diff">
      <p>v{{ diff.fromVersion }} → v{{ diff.toVersion }}</p>
      <p v-if="diff.addedSteps.length">{{ $t("新增工步：{0}", [diff.addedSteps.join("、")]) }}</p>
      <p v-if="diff.removedSteps.length">{{ $t("删除工步：{0}", [diff.removedSteps.join("、")]) }}</p>
      <p v-if="!diff.changes.length && !diff.addedSteps.length && !diff.removedSteps.length" class="muted">
        {{ $t("两个版本工艺内容相同") }}
      </p>
      <!-- 只有"整步增删"而字段没变时，上面两行已经说完了；再摊一张空表出来，
          表里的"暂无数据"会和上面那句"新增工步"读起来自相矛盾。 -->
      <el-table v-if="diff.changes.length" :data="diff.changes" size="small" max-height="360">
        <el-table-column prop="path" :label="$t('路径')" min-width="180" fixed />
        <el-table-column prop="before" :label="$t('之前')" />
        <el-table-column prop="after" :label="$t('之后')" />
      </el-table>
    </div>
  </el-dialog>
</template>

<script setup lang="ts">
import type { RecipeVersionDiffDto } from "../api/types";

/** 版本差异：取数与"跟哪个版本比"留在页面，这里只摊开结果。 */
defineProps<{ diff: RecipeVersionDiffDto | null }>();
const visible = defineModel<boolean>({ required: true });
</script>

<style scoped>
.muted { color: var(--muted); font-size: 12px; }
</style>
