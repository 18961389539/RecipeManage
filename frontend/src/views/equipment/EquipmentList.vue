<template>
  <div>
    <div class="page-title">
      <div>
        <h2>{{ $t("设备与 PLC 驱动") }}</h2>
        <span>{{ $t("产线设备、连接参数，以及设备类上的相模板。") }}</span>
      </div>
      <div>
        <el-button v-if="equipmentPane === 'devices' && auth.can('Admin')" type="primary" @click="open(null)">{{ $t("新增设备") }}</el-button>
      </div>
    </div>
    <div class="filter-bar">
      <HelpTip term="聚焦本页搜索" chord="/" plain placement="bottom">
        <el-input
          v-model="query"
          class="search-field"
          clearable
          data-shortcut-search
          :placeholder="equipmentPane === 'library' ? $t('搜索设备类 / 相模板') : $t('搜索编码 / 名称 / 主机')"
        />
      </HelpTip>
      <span v-if="!loading" class="result-count">{{ countText }}</span>
    </div>
    <el-alert class="gap-after"
      v-if="error"
      :closable="false"
      type="error"
      :title="$t('设备列表加载失败：{0}', [error])"
      show-icon
    />
    <el-tabs v-model="equipmentPane" class="equipment-tabs">
      <el-tab-pane name="devices">
        <template #label>
          <span class="tab-label"><HelpTip term="设备驱动">{{ $t("设备") }}</HelpTip></span>
        </template>
        <el-table :data="shown" v-loading="loading" scrollbar-always-on max-height="calc(100vh - 340px)" :empty-text="query.trim() ? $t('没有匹配的设备') : $t('暂无设备')">
          <el-table-column prop="code" :label="$t('编码')" width="100" fixed sortable :sort-method="sorters.code" />
          <el-table-column prop="name" :label="$t('名称')" sortable :sort-method="sorters.name" />
          <el-table-column prop="protocol" :label="$t('协议')" width="120" sortable :sort-method="sorters.protocol">
            <template #default="{ row }">{{ protocolLabel(row.protocol) }}</template>
          </el-table-column>
          <!-- OPC UA 的 host 是整条端点（opc.tcp://host:port/路径），不限宽会把端口列挤成三行错位；
               定宽 + 溢出 tooltip，完整地址悬停可见 -->
          <el-table-column prop="host" :label="$t('主机')" width="180" show-overflow-tooltip sortable :sort-method="sorters.host" />
          <el-table-column prop="port" :label="$t('端口')" width="80" sortable :sort-method="sorters.port" />
          <el-table-column prop="plcModel" :label="$t('型号')" width="120" />
          <el-table-column prop="equipmentClassCode" :label="$t('设备类')" width="110" sortable :sort-method="sorters.equipmentClassCode">
            <template #default="{ row }">{{ row.equipmentClassCode || $t("未分类") }}</template>
          </el-table-column>
          <el-table-column prop="enabled" :label="$t('启用')" width="80" sortable :sort-method="sorters.enabled" :sort-orders="DESC_FIRST">
            <template #default="{ row }">{{ row.enabled ? $t("是") : $t("否") }}</template>
          </el-table-column>
          <el-table-column prop="occupancy" :label="$t('占用')" width="160" sortable :sort-method="sorters.occupancy">
            <template #header><HelpTip term="设备占用" /></template>
            <template #default="{ row }">
              <span v-if="row.occupancy === 'Occupied'" class="occ" @click.stop="openOccupant(row)">{{ row.occupyingBatchNo }}</span>
              <span v-else>{{ $t("空闲") }}</span>
            </template>
          </el-table-column>
          <el-table-column :label="$t('操作')" width="360">
            <template #default="{ row }">
              <el-button link type="primary" @click="open(row)">{{ auth.can('Admin') ? $t("编辑") : $t("查看") }}</el-button>
              <el-button v-if="auth.can('Admin', 'Operator', 'Supervisor')" link type="primary" :loading="busy === `test:${row.id}`" @click="testConn(row)">{{ $t("测试连接") }}</el-button>
              <el-button v-if="auth.can('Admin')" link type="primary" :loading="busy === `validate:${row.id}`" @click="validate(row)">{{ $t("校验点表") }}</el-button>
              <el-dropdown v-if="faultInjectable(row) && auth.can('Operator', 'Supervisor')" @command="(mode: string) => inject(row, mode)">
                <el-button link type="warning" :loading="busy === `inject:${row.id}`">{{ $t("仿真故障") }}</el-button>
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item v-for="m in injectModes" :key="m.mode" :command="m.mode" :divided="m.mode === 'None'">{{ m.label }}</el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
            </template>
          </el-table-column>
        </el-table>
        <el-alert class="gap-before" type="info" show-icon :title="$t('驱动层解耦：Simulator / Siemens S7（IoTClient） / Modbus TCP（IoTClient） / OPC UA（OPC Foundation）。握手变量集合固定，禁止绕过 PLC_Ready 盲写。')" />
      </el-tab-pane>
      <el-tab-pane name="library">
        <template #label>
          <span class="tab-label"><HelpTip term="相库">{{ $t("相库") }}</HelpTip></span>
        </template>
        <PhaseLibraryPanel
          :classes="shownClasses"
          :can-edit="canEditLibrary"
          :focus-class-id="focusClass?.id ?? ''"
          :expand-all="equipmentPane === 'library' && !!query.trim()"
          @changed="load"
        />
      </el-tab-pane>
    </el-tabs>

    <EquipmentFormDialog v-model="formVisible" :equipment="editing" :classes="classes" :can-edit="auth.can('Admin')" @saved="load" />
  </div>
</template>

<script setup lang="ts">
import { t } from "../../i18n";
import { computed, onMounted, ref } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessage, ElMessageBox } from "element-plus";
import {
  injectSimulatorFault, listEquipment, listEquipmentClasses, testConnection, validateTagMap
} from "../../api/equipment";
import type { EquipmentClassDto, EquipmentDto, ExecutionEvent } from "../../api/types";
import { applyOccupancyToEquipment, occupancyFromEvent, useExecutionHub } from "../../realtime/executionHub";
import { useCoalescedReload } from "../../utils/useCoalescedReload";
import { usePolling } from "../../utils/usePolling";
import { useAuthStore } from "../../stores/auth";
import { occupancyOrder, protocolLabel } from "../../utils/labels";
import { matchesQuery } from "../../utils/format";
import { useLoad } from "../../utils/useLoad";
import { DESC_FIRST, byEnum, byNumber, byText } from "../../utils/tableSort";
import { templateProgram } from "../../utils/phaseTemplate";
import HelpTip from "../../components/HelpTip.vue";
import EquipmentFormDialog from "../../components/EquipmentFormDialog.vue";
import PhaseLibraryPanel from "../../components/PhaseLibraryPanel.vue";

/**
 * 设备页只剩"列表 + 三个行内动作 + 实时刷新"。
 * 设备表单在 EquipmentFormDialog、相库在 PhaseLibraryPanel / PhaseTemplateDialog，
 * 点表 JSON 的翻译在 utils/tagMap（唯一可单测的那部分）。
 */
const auth = useAuthStore();
const route = useRoute();
const router = useRouter();
const items = ref<EquipmentDto[]>([]);
const query = ref("");
const classes = ref<EquipmentClassDto[]>([]);
const busy = ref("");
const { loading, error, runValue } = useLoad();
const canEditLibrary = computed(() => auth.can("Admin", "ProcessEngineer"));
const formVisible = ref(false);
const editing = ref<EquipmentDto | null>(null);

type EquipmentPane = "devices" | "library";
const equipmentPane = computed<EquipmentPane>({
  get: () => (route.query.tab === "library" ? "library" : "devices"),
  set: (value) => {
    const next = { ...route.query };
    if (value === "library") next.tab = "library";
    else {
      delete next.tab;
      delete next.class;
    }
    void router.replace({ query: next });
  }
});

const shownClasses = computed(() => {
  const q = query.value;
  if (equipmentPane.value !== "library" || !q.trim()) return classes.value;
  return classes.value.flatMap((cls) => {
    const classHit = matchesQuery(q, cls.code, cls.name, cls.description);
    const templates = classHit
      ? cls.templates
      : cls.templates.filter((t) => matchesQuery(q, t.code, t.name, t.operation, String(templateProgram(t))));
    if (!classHit && !templates.length) return [];
    return [{ ...cls, templates }];
  });
});

/** 设计器与设备页用 `?tab=library&class=CODE` 深链到某个设备类，面板据此只展开它。 */
const focusClass = computed(() => {
  const raw = route.query.class;
  const code = typeof raw === "string" ? raw.trim().toUpperCase() : "";
  return code ? classes.value.find((c) => c.code.toUpperCase() === code) ?? null : null;
});

const shown = computed(() =>
  items.value.filter((row) =>
    matchesQuery(
      query.value,
      row.code,
      row.name,
      row.host,
      row.plcModel,
      row.equipmentClassCode,
      protocolLabel(row.protocol),
      row.occupyingBatchNo
    )
  )
);
/** 排序口径见 utils/tableSort：协议/主机名按拼音，占用按次序（被占的在前）。 */
const sorters = {
  code: byText<EquipmentDto>((r) => r.code),
  name: byText<EquipmentDto>((r) => r.name),
  protocol: byText<EquipmentDto>((r) => protocolLabel(r.protocol)),
  host: byText<EquipmentDto>((r) => r.host),
  port: byNumber<EquipmentDto>((r) => r.port),
  equipmentClassCode: byText<EquipmentDto>((r) => r.equipmentClassCode),
  enabled: byNumber<EquipmentDto>((r) => (r.enabled ? 1 : 0)),
  occupancy: byEnum<EquipmentDto>((r) => r.occupancy, occupancyOrder)
};
const countText = computed(() => {
  if (equipmentPane.value === "library") {
    const total = classes.value.reduce((sum, cls) => sum + cls.templates.length, 0);
    const n = shownClasses.value.reduce((sum, cls) => sum + cls.templates.length, 0);
    return query.value.trim() ? t("{0} / {1} 个相模板", n, total) : t("共 {0} 个相模板", total);
  }
  const n = shown.value.length;
  const total = items.value.length;
  return query.value.trim() ? t("{0} / {1} 台", n, total) : t("共 {0} 台", total);
});

/** computed 而非模块常量：t() 要在语言切换后重算，写死在数组里会冻结在加载时的语言。 */
const injectModes = computed(() => [
  { mode: "HoldNotReady", label: t("保持未 Ready（禁止写参）") },
  { mode: "CorruptEcho", label: t("回读不一致（拒绝 Trigger_Write）") },
  { mode: "NoAck", label: t("Trigger 后不应答") },
  { mode: "StepError", label: t("PLC 报 Step_Error") },
  { mode: "DropHeartbeat", label: t("丢失心跳") },
  { mode: "None", label: t("清除故障") }
]);
/** 只有环回/仿真驱动能被注入故障，真实 PLC 上这个菜单必须不出现。 */
function faultInjectable(row: EquipmentDto) {
  return ["Simulator", "ModbusTcp", "OpcUa", "SiemensS7"].includes(row.protocol);
}

// 轮询也必须走合并窗口：绕过 scheduleReload 会和事件驱动的重拉叠打出重复请求。
const poll = usePolling(() => scheduleReload());

async function load() {
  // 原先裸 await：4 秒轮询一旦失败会持续抛未处理 rejection，界面也不会有任何提示。
  await runValue(listEquipment(), (d) => (items.value = d));
  await runValue(listEquipmentClasses(), (d) => (classes.value = d));
}

/** 复制一份再交给对话框：表单编辑不能改动列表里的那一行，否则取消后界面会留着没保存的值。 */
function open(row: EquipmentDto | null) {
  editing.value = row ? { ...row } : null;
  formVisible.value = true;
}

function openOccupant(row: EquipmentDto) {
  if (row.occupyingBatchId) router.push(`/batches/${row.occupyingBatchId}`);
}

async function validate(row: EquipmentDto) {
  // 键里带动作：一行有三个操作按钮，只写 row.id 会让没被点的那两个也一起转。
  busy.value = `validate:${row.id}`;
  try {
    ElMessage.success((await validateTagMap(row.id)).message);
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    busy.value = "";
  }
}

async function testConn(row: EquipmentDto) {
  // OPC UA 连不上要等超时，好几秒没有任何反馈，不加 loading 会被当成没点上而反复点。
  busy.value = `test:${row.id}`;
  try {
    const data = await testConnection(row.id);
    if (data.connected) ElMessage.success(`${data.message} (${data.latencyMs.toFixed(0)} ms)`);
    else ElMessage.error(data.message);
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    busy.value = "";
  }
}

async function inject(row: EquipmentDto, mode: string) {
  const label = injectModes.value.find((m) => m.mode === mode)?.label ?? mode;
  try {
    await ElMessageBox.confirm(
      mode === "None"
        ? t("清除 {0} 上的仿真故障？", row.code)
        : t("向 {0} 注入「{1}」？若该设备上有运行中批次，会进入故障或保持。", row.code, label),
      t("仿真故障"),
      {
        type: "warning",
        confirmButtonText: mode === "None" ? t("清除") : t("注入"),
        cancelButtonText: t("取消")
      }
    );
  } catch {
    return;
  }
  busy.value = `inject:${row.id}`;
  try {
    ElMessage.warning((await injectSimulatorFault(row.id, mode)).message);
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    busy.value = "";
  }
}

onMounted(async () => {
  await load();
  poll.start();
});

// 非占用事件（握手约 100ms 一条）带来的重拉必须合并。
const scheduleReload = useCoalescedReload(load);

function onExecution(evt: ExecutionEvent) {
  const occupancy = occupancyFromEvent(evt);
  if (occupancy)
    items.value = applyOccupancyToEquipment(items.value, occupancy);
  else
    scheduleReload();
}

useExecutionHub({ onExecution });
</script>

<style scoped>
.occ { cursor: pointer; color: var(--accent-bright); }
.equipment-tabs :deep(.el-tabs__header) { margin-bottom: var(--space-3); }
.equipment-tabs :deep(.el-tabs__item) { padding: 0 16px; }
.tab-label { display: inline-flex; align-items: center; gap: 6px; }
</style>
