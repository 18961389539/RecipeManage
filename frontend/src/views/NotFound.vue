<template>
  <el-result icon="warning" title="页面不存在">
    <template #sub-title>
      <p>没有找到地址 <code>{{ path }}</code> 对应的页面，可能是链接已过时或地址输入有误。</p>
    </template>
    <template #extra>
      <el-button type="primary" @click="$router.replace('/dashboard')">回到运行总览</el-button>
      <el-button @click="back">返回上一页</el-button>
    </template>
  </el-result>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { useRoute, useRouter } from "vue-router";

const route = useRoute();
const router = useRouter();
// 兜底路由匹配任意路径，用 fullPath 把用户原本要去的地址回显出来，便于核对拼写。
const path = computed(() => route.fullPath);

function back() {
  if (window.history.state?.back) router.back();
  else router.replace("/dashboard");
}
</script>
