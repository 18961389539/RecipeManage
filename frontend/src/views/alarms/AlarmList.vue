<template>
  <div>
    <div class="page-title">
      <h2>过程报警</h2>
      <el-radio-group v-model="filter" size="small">
        <el-radio-button value="open">未确认</el-radio-button>
        <el-radio-button value="all">全部</el-radio-button>
      </el-radio-group>
    </div>
    <el-alert class="gap-after"
      :closable="false"
      type="info"
      show-icon
      title="握手故障、质检超差、调度引擎异常会写入报警履历。确认后仍保留记录，供批次放行与追溯。"
     
    />
    <el-alert class="gap-after"
      v-if="error"
      :closable="false"
      type="error"
      :title="`报警列表加载失败：${error}`"
      show-icon
     
    />
    <el-table
      :data="visible"
      v-loading="loading"
      class="clickable-rows"
      :empty-text="filter === 'open' ? '暂无未确认报警' : '暂无过程报警'"
      @row-click="(row: ProcessAlarmDto) => $router.push(`/batches/${row.batchId}`)"
    >
      <el-table-column prop="raisedAt" label="时间" width="180" fixed>
        <template #default="{ row }">{{ formatDateTime(row.raisedAt) }}</template>
      </el-table-column>
      <el-table-column prop="batchNo" label="批次" width="160" />
      <el-table-column prop="stepCode" label="工步" width="80" />
      <el-table-column prop="code" label="代码" width="140" />
      <el-table-column prop="severity" label="级别" width="90">
        <template #default="{ row }">
          <el-tag size="small" :type="alarmSeverityTagType(row.severity)" effect="dark">{{ alarmSeverityLabel(row.severity) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="message" label="说明" />
      <el-table-column label="确认" width="140">
        <template #default="{ row }">
          <span v-if="row.acknowledgedAt">{{ row.acknowledgedBy }}</span>
          <el-button v-else-if="auth.can('Operator', 'Supervisor', 'Quality')" link type="primary"
            :loading="acking === row.id" @click.stop="ack(row)">确认</el-button>
          <span v-else>未确认</span>
        </template>
      </el-table-column>
    </el-table>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { ElMessage } from "element-plus";
import http from "../../api/http";
import type { ProcessAlarmDto } from "../../api/types";
import { useAuthStore } from "../../stores/auth";
import { useExecutionHub } from "../../realtime/executionHub";
import { alarmSeverityLabel, alarmSeverityTagType } from "../../utils/labels";
import { formatDateTime } from "../../utils/format";
import { useLoad } from "../../utils/useLoad";
import { useCoalescedReload } from "../../utils/useCoalescedReload";

const auth = useAuthStore();
const items = ref<ProcessAlarmDto[]>([]);
const filter = ref<"open" | "all">("open");
const acking = ref("");
const { loading, error, run } = useLoad();

const visible = computed(() =>
  filter.value === "open" ? items.value.filter((a) => !a.acknowledgedAt) : items.value
);

async function load() {
  await run(http.get<ProcessAlarmDto[]>("/alarms"), (d) => (items.value = d));
}

async function ack(row: ProcessAlarmDto) {
  acking.value = row.id;
  try {
    await http.post(`/alarms/${row.id}/ack`);
    await load();
    // 「未确认」筛选下确认成功后整行会消失，不提示容易被当成误操作或记录丢失。
    ElMessage.success(`已确认报警 ${row.code}`);
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    acking.value = "";
  }
}

// 报警/保持事件在一段异常里会连串推送，合并窗口避免每次都重拉整张履历表。
const scheduleReload = useCoalescedReload(load);

useExecutionHub({
  onExecution: (evt) => {
    if (evt.type === "alarm" || evt.type === "fault" || evt.type === "held" || evt.type === "occupancy")
      scheduleReload();
  }
});

onMounted(load);
</script>
