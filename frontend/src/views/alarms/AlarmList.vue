<template>
  <div>
    <div class="page-title">
      <div>
        <h2>{{ $t("过程报警") }}<PageGuideButton guide-key="alarms" /></h2>
        <span>{{ $t("握手故障、质检超差和调度异常。确认后仍保留，供放行与追溯。") }}</span>
      </div>
      <div>
        <el-button
          v-if="canAck && openCount > 0"
          type="primary"
          :loading="acking === 'bulk'"
          :disabled="!!acking"
          @click="ackVisible"
        >{{ $t("确认本页 {0} 条", [openCount]) }}</el-button>
      </div>
    </div>
    <div class="filter-bar">
      <HelpTip term="聚焦本页搜索" chord="/" plain placement="bottom">
        <el-input v-model="query" class="search-field" clearable data-shortcut-search :placeholder="$t('搜索批次 / 代码 / 说明')" />
      </HelpTip>
      <span v-if="!loading" class="result-count">{{ countText }}</span>
      <el-radio-group v-model="filter" size="small" class="filter-chips">
        <el-radio-button value="open">{{ $t("未确认") }}</el-radio-button>
        <el-radio-button value="all">{{ $t("全部") }}</el-radio-button>
      </el-radio-group>
    </div>
    <el-alert class="gap-after"
      v-if="error"
      :closable="false"
      type="error"
      :title="$t('报警列表加载失败：{0}', [error])"
      show-icon
     
    />
    <p v-if="isMobile" class="mobile-table-hint">{{ $t("窄屏下左右滑动表格查看其余列和行操作。") }}</p>
    <el-table
      ref="tableRef"
      :data="items"
      v-loading="loading"
      class="clickable-rows alarm-table"
      scrollbar-always-on
      max-height="calc(100vh - 292px)"
      :empty-text="emptyText"
      :default-sort="defaultSort"
      :row-class-name="alarmRowClass"
      @sort-change="onSortChange"
      @row-click="(row: ProcessAlarmDto) => $router.push(`/batches/${row.batchId}`)"
    >
      <el-table-column prop="raisedAt" :label="$t('时间')" width="180" fixed sortable="custom" :sort-orders="SERVER_DESC_FIRST">
        <template #default="{ row }">{{ formatDateTime(row.raisedAt) }}</template>
      </el-table-column>
      <el-table-column prop="batchNo" :label="$t('批次')" width="160" sortable="custom" :sort-orders="SERVER_ASC_FIRST" />
      <el-table-column prop="stepCode" :label="$t('工步')" width="92" sortable="custom" :sort-orders="SERVER_ASC_FIRST" />
      <el-table-column prop="code" :label="$t('代码')" width="140" sortable="custom" :sort-orders="SERVER_ASC_FIRST" />
      <el-table-column prop="severity" :label="$t('级别')" width="104" sortable="custom" :sort-orders="SERVER_ASC_FIRST">
        <template #default="{ row }">
          <el-tag size="small" :type="alarmSeverityTagType(row.severity)" effect="dark">{{ alarmSeverityLabel(row.severity) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="message" :label="$t('说明')" min-width="220" show-overflow-tooltip />
      <!-- 「确认」在桌面/平板固定右侧；手机解除固定以留出表格内容空间，仍可横向滑动逐条确认。 -->
      <el-table-column prop="acknowledgedAt" :label="$t('确认')" width="140" :fixed="isMobile ? false : 'right'" sortable="custom" :sort-orders="SERVER_ASC_FIRST">
        <template #default="{ row }">
          <span v-if="row.acknowledgedAt">{{ row.acknowledgedBy }}</span>
          <el-button v-else-if="auth.can('Operator', 'Supervisor', 'Quality')" link type="primary"
            :loading="acking === row.id" :disabled="!!acking" @click.stop="ack(row)">{{ $t("确认") }}</el-button>
          <span v-else>{{ $t("未确认") }}</span>
        </template>
      </el-table-column>
    </el-table>
    <el-pagination class="pager" layout="total, prev, pager, next" background small :page-size="take"
      :current-page="page" :total="total" hide-on-single-page @current-change="onPageChange" />
  </div>
</template>

<script setup lang="ts">
import { t } from "../../i18n";
import { computed, onMounted, ref, watch } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import type { TableInstance } from "element-plus";
import http from "../../api/http";
import type { ProcessAlarmDto, ProcessAlarmPageDto } from "../../api/types";
import { useAuthStore } from "../../stores/auth";
import { useAlarmBadgeStore } from "../../stores/alarms";
import { useExecutionHub } from "../../realtime/executionHub";
import { alarmSeverityLabel, alarmSeverityTagType } from "../../utils/labels";
import { formatDateTime } from "../../utils/format";
import { useLoad } from "../../utils/useLoad";
import { useCoalescedReload } from "../../utils/useCoalescedReload";
import { useKeyboardRows } from "../../utils/useKeyboardRows";
import { SERVER_ASC_FIRST, SERVER_DESC_FIRST } from "../../utils/tableSort";
import { useServerPaging } from "../../utils/useServerPaging";
import { useIsMobile } from "../../utils/useMedia";
import HelpTip from "../../components/HelpTip.vue";

const auth = useAuthStore();
const isMobile = useIsMobile();
const alarmBadge = useAlarmBadgeStore();
const items = ref<ProcessAlarmDto[]>([]);
const tableRef = ref<TableInstance>();
const query = ref("");
const filter = ref<"open" | "all">("open");
const acking = ref("");
const { loading, error, run } = useLoad();

/**
 * 搜索、"未确认"筛选、排序、分页全交给服务端。
 * 客户端过滤只能看到当页那 50 条：未确认的老报警会被"最近 50 条"挤出去，
 * 操作员在页面上看不见，也就永远不会去确认——而报警必须确认掉才算闭环。
 */
const pg = useServerPaging({ reload: load, defaultSort: "raisedAt", search: query });
const { take, total, page, defaultSort, onSortChange, onPageChange } = pg;
const countText = computed(() => {
  const n = items.value.length;
  const filtered = !!query.value.trim() || filter.value !== "all";
  return filtered ? t("{0} / {1} 条", n, total.value) : t("共 {0} 条", total.value);
});
const emptyText = computed(() => {
  if (query.value.trim()) return t("没有匹配的报警");
  return filter.value === "open" ? t("暂无未确认报警") : t("暂无过程报警");
});
const canAck = computed(() => auth.can("Operator", "Supervisor", "Quality"));
const openCount = computed(() => items.value.filter((a) => !a.acknowledgedAt).length);
function alarmRowClass({ row }: { row: ProcessAlarmDto }) {
  return row.acknowledgedAt ? "" : "alarm-pending";
}
// 整行可点，但 EP 渲染的 tr 不可聚焦——键盘用户此前打不开任何报警。
useKeyboardRows(tableRef, () => items.value);

watch(filter, () => pg.onFilterChange());

async function load() {
  await run(
    http.get<ProcessAlarmPageDto>("/alarms", {
      params: pg.params({ onlyOpen: filter.value === "open" ? "true" : undefined })
    }),
    (d) => {
      items.value = d.items;
      total.value = d.total;
    }
  );
}

async function ack(row: ProcessAlarmDto) {
  if (acking.value) return;
  acking.value = row.id;
  try {
    await http.post(`/alarms/${row.id}/ack`);
    await load();
    // 侧栏徽标跟着减：确认完数字还挂着会像没生效。
    void alarmBadge.refresh();
    // 「未确认」筛选下确认成功后整行会消失，不提示容易被当成误操作或记录丢失。
    ElMessage.success(t("已确认报警 {0}", row.code));
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    acking.value = "";
  }
}

async function ackVisible() {
  if (acking.value) return;
  const pending = items.value.filter((a) => !a.acknowledgedAt);
  if (!pending.length) return;
  try {
    await ElMessageBox.confirm(
      t("确认当前列表中的 {0} 条未确认报警？确认后仍保留履历，供批次放行与追溯。", pending.length),
      t("批量确认报警"),
      { type: "warning", confirmButtonText: t("全部确认"), cancelButtonText: t("取消") }
    );
  } catch {
    return;
  }
  acking.value = "bulk";
  let ok = 0;
  try {
    for (const row of pending) {
      await http.post(`/alarms/${row.id}/ack`);
      ok += 1;
    }
    await load();
    ElMessage.success(t("已确认 {0} 条报警", ok));
  } catch (e) {
    await load();
    ElMessage.error(t("已确认 {0} 条，其余失败：{1}", ok, (e as Error).message));
  } finally {
    acking.value = "";
    // 成功与部分失败都刷新：批量里已经确认掉的那几条要让侧栏徽标立刻反映。
    void alarmBadge.refresh();
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

<style scoped>
.alarm-table :deep(.alarm-pending > td:first-child) {
  box-shadow: inset 3px 0 0 var(--warn);
}
</style>
