<template>
  <div>
    <div class="page-title">
      <h2>设备与 PLC 驱动</h2>
      <el-button v-if="auth.can('Admin')" type="primary" @click="open()">新增设备</el-button>
    </div>
    <el-alert class="gap-after"
      v-if="error"
      :closable="false"
      type="error"
      :title="`设备列表加载失败：${error}`"
      show-icon
     
    />
    <el-table :data="items" v-loading="loading" empty-text="暂无设备">
      <el-table-column prop="code" label="编码" width="100" fixed />
      <el-table-column prop="name" label="名称" />
      <el-table-column prop="protocol" label="协议" width="120">
        <template #default="{ row }">{{ protocolLabel(row.protocol) }}</template>
      </el-table-column>
      <el-table-column prop="host" label="主机" />
      <el-table-column prop="port" label="端口" width="80" />
      <el-table-column prop="plcModel" label="型号" width="120" />
      <el-table-column prop="equipmentClassCode" label="设备类" width="110">
        <template #default="{ row }">{{ row.equipmentClassCode || "未分类" }}</template>
      </el-table-column>
      <el-table-column prop="enabled" label="启用" width="80">
        <template #default="{ row }">{{ row.enabled ? "是" : "否" }}</template>
      </el-table-column>
      <el-table-column label="占用" width="160">
        <template #header><HelpTip term="设备占用" /></template>
        <template #default="{ row }">
          <span v-if="row.occupancy === 'Occupied'" class="occ" @click.stop="openOccupant(row)">{{ row.occupyingBatchNo }}</span>
          <span v-else>空闲</span>
        </template>
      </el-table-column>
      <el-table-column label="" width="360">
        <template #default="{ row }">
          <el-button link type="primary" @click="open(row)">{{ auth.can('Admin') ? "编辑" : "查看" }}</el-button>
          <el-button v-if="auth.can('Admin', 'Operator', 'Supervisor')" link type="primary" :loading="busy === `test:${row.id}`" @click="testConn(row)">测试连接</el-button>
          <el-button v-if="auth.can('Admin')" link type="primary" :loading="busy === `validate:${row.id}`" @click="validate(row)">校验点表</el-button>
          <el-dropdown v-if="(row.protocol === 'Simulator' || row.protocol === 'ModbusTcp' || row.protocol === 'OpcUa' || row.protocol === 'SiemensS7') && auth.can('Operator', 'Supervisor')" @command="(mode: string) => inject(row, mode)">
            <el-button link type="warning" :loading="busy === `inject:${row.id}`">仿真故障</el-button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item command="HoldNotReady">保持未 Ready（禁止写参）</el-dropdown-item>
                <el-dropdown-item command="CorruptEcho">回读不一致（拒绝 Trigger_Write）</el-dropdown-item>
                <el-dropdown-item command="NoAck">Trigger 后不应答</el-dropdown-item>
                <el-dropdown-item command="StepError">PLC 报 Step_Error</el-dropdown-item>
                <el-dropdown-item command="DropHeartbeat">丢失心跳</el-dropdown-item>
                <el-dropdown-item command="None" divided>清除故障</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </template>
      </el-table-column>
    </el-table>
    <el-alert class="gap-before" type="info" show-icon title="驱动层解耦：Simulator / Siemens S7（IoTClient） / Modbus TCP（IoTClient） / OPC UA（OPC Foundation）。握手变量集合固定，禁止绕过 PLC_Ready 盲写。" />

    <el-dialog v-model="visible" :title="form.id ? '设备 / 握手点表' : '新增设备'" width="720px">
      <el-form label-width="110px" :disabled="!auth.can('Admin')">
        <el-form-item label="编码"><el-input v-model="form.code" :disabled="!!form.id" /></el-form-item>
        <el-form-item label="名称"><el-input v-model="form.name" /></el-form-item>
        <el-form-item label="协议">
          <el-select v-model="form.protocol" style="width:100%">
            <el-option label="Simulator" value="Simulator" />
            <el-option label="Siemens S7" value="SiemensS7" />
            <el-option label="Modbus TCP" value="ModbusTcp" />
            <el-option label="OPC UA" value="OpcUa" />
          </el-select>
        </el-form-item>
        <el-form-item label="主机"><el-input v-model="form.host" placeholder="IP 或 opc.tcp://host:4840" /></el-form-item>
        <el-form-item label="端口"><el-input-number v-model="form.port" /></el-form-item>
        <el-form-item label="型号"><el-input v-model="form.plcModel" /></el-form-item>
        <el-form-item label="Rack/Slot">
          <el-input-number v-model="form.rack" /> / <el-input-number v-model="form.slot" />
        </el-form-item>
        <el-form-item label="启用"><el-switch v-model="form.enabled" /></el-form-item>
        <el-form-item label="设备类">
          <el-select v-model="form.equipmentClassCode" clearable placeholder="未分类则跳过相能力校验" style="width:100%">
            <el-option v-for="c in classes" :key="c.code" :label="`${c.code} · ${c.name}`" :value="c.code" />
          </el-select>
        </el-form-item>
        <el-divider>握手看门狗（秒）</el-divider>
        <el-form-item label="等待 Ready"><el-input-number v-model="watchdog.readyWaitSeconds" :min="2" /></el-form-item>
        <el-form-item label="写参超时"><el-input-number v-model="watchdog.writeTimeoutSeconds" :min="1" /></el-form-item>
        <el-form-item label="应答超时"><el-input-number v-model="watchdog.ackTimeoutSeconds" :min="1" /></el-form-item>
        <el-form-item label="心跳超时"><el-input-number v-model="watchdog.heartbeatTimeoutSeconds" :min="1" /></el-form-item>
        <el-form-item label="复位超时"><el-input-number v-model="watchdog.resetTimeoutSeconds" :min="1" /></el-form-item>
        <el-form-item label="保持应答"><el-input-number v-model="watchdog.holdAckSeconds" :min="1" /></el-form-item>
        <el-divider>握手点表（禁止缺 PLC_Ready / Trigger_Write）</el-divider>
        <el-form-item>
          <template #label>Step_ID <HelpTip term="Step_ID" /></template>
          <el-input v-model="tagMap.stepId" />
        </el-form-item>
        <el-form-item>
          <template #label>Step_Type <HelpTip term="Step_Type" /></template>
          <el-input v-model="tagMap.stepType" />
        </el-form-item>
        <el-form-item>
          <template #label>Trigger_Write <HelpTip term="Trigger_Write" /></template>
          <el-input v-model="tagMap.triggerWrite" />
        </el-form-item>
        <el-form-item>
          <template #label>PLC_Ready <HelpTip term="PLC_Ready" /></template>
          <el-input v-model="tagMap.plcReady" />
        </el-form-item>
        <el-form-item>
          <template #label>Step_Running <HelpTip term="Step_Running" /></template>
          <el-input v-model="tagMap.stepRunning" />
        </el-form-item>
        <el-form-item>
          <template #label>Step_Complete <HelpTip term="Step_Complete" /></template>
          <el-input v-model="tagMap.stepComplete" />
        </el-form-item>
        <el-form-item>
          <template #label>Step_Error <HelpTip term="Step_Error" /></template>
          <el-input v-model="tagMap.stepError" />
        </el-form-item>
        <el-form-item>
          <template #label>Host_Hold <HelpTip term="Host_Hold" /></template>
          <el-input v-model="tagMap.hostHold" />
        </el-form-item>
        <el-form-item>
          <template #label>PLC_Held <HelpTip term="PLC_Held" /></template>
          <el-input v-model="tagMap.plcHeld" />
        </el-form-item>
        <el-form-item>
          <template #label>Error_Code <HelpTip term="Error_Code" /></template>
          <el-input v-model="tagMap.errorCode" />
        </el-form-item>
        <el-form-item>
          <template #label>Heartbeat <HelpTip term="Heartbeat" /></template>
          <el-input v-model="tagMap.heartbeat" />
        </el-form-item>
        <el-form-item>
          <template #label>Temperature <HelpTip term="Temperature" /></template>
          <el-input v-model="tagMap.measured.Temperature" />
        </el-form-item>
        <el-form-item>
          <template #label>HoldTime <HelpTip term="HoldTime" /></template>
          <el-input v-model="tagMap.measured.HoldTime" />
        </el-form-item>
        <el-form-item>
          <template #label>Pressure <HelpTip term="Pressure" /></template>
          <el-input v-model="tagMap.measured.Pressure" />
        </el-form-item>
        <template v-if="form.protocol === 'OpcUa'">
          <el-divider>OPC UA 安全（默认不自动接受证书、匿名）</el-divider>
          <el-form-item label="签名端点"><el-switch v-model="tagMap.opcUaUseSecurity" /></el-form-item>
          <el-form-item label="接受自签证书"><el-switch v-model="tagMap.opcUaAutoAcceptCertificates" /></el-form-item>
          <el-form-item label="用户名"><el-input v-model="tagMap.opcUaUser" placeholder="留空为匿名" /></el-form-item>
          <el-form-item label="密码"><el-input v-model="tagMap.opcUaPassword" type="password" show-password /></el-form-item>
        </template>
      </el-form>
      <template #footer>
        <el-button @click="visible = false">关闭</el-button>
        <el-button v-if="auth.can('Admin')" type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from "vue";
import { useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import http from "../../api/http";
import type { ConnectionTestDto, EquipmentClassDto, EquipmentDto, ExecutionEvent, PlcProtocol, TagMapCheckDto } from "../../api/types";
import { applyOccupancyToEquipment, occupancyFromEvent, useExecutionHub } from "../../realtime/executionHub";
import { useCoalescedReload } from "../../utils/useCoalescedReload";
import { usePolling } from "../../utils/usePolling";
import { useAuthStore } from "../../stores/auth";
import { protocolLabel } from "../../utils/labels";
import { useLoad } from "../../utils/useLoad";
import HelpTip from "../../components/HelpTip.vue";

const auth = useAuthStore();
const router = useRouter();
const items = ref<EquipmentDto[]>([]);
const classes = ref<EquipmentClassDto[]>([]);
const saving = ref(false);
const busy = ref("");
const { loading, error, run } = useLoad();
// 轮询也必须走合并窗口：绕过 scheduleReload 会和事件驱动的重拉叠打出重复请求。
const poll = usePolling(() => scheduleReload());
const visible = ref(false);
const form = reactive({
  id: "",
  code: "",
  name: "",
  protocol: "Simulator" as PlcProtocol,
  host: "127.0.0.1",
  port: 102,
  plcModel: "S7_1200",
  rack: 0,
  slot: 1,
  enabled: true,
  tagMapJson: "{}",
  description: "",
  watchdogJson: "",
  equipmentClassCode: "" as string
});
const tagMap = reactive(emptyMap());
const watchdog = reactive({
  readyWaitSeconds: 15,
  writeTimeoutSeconds: 5,
  ackTimeoutSeconds: 8,
  heartbeatTimeoutSeconds: 3,
  resetTimeoutSeconds: 8,
  idleSettleSeconds: 10,
  holdAckSeconds: 8
});

function emptyMap() {
  const params: string[] = [];
  for (let i = 0; i < 16; i++) params.push(`DB10.${20 + i * 4}`);
  return {
    stepId: "DB10.0",
    stepType: "DB10.4",
    triggerWrite: "DB10.8.0",
    plcReady: "DB10.8.1",
    stepRunning: "DB10.8.2",
    stepComplete: "DB10.8.3",
    stepError: "DB10.8.4",
    hostHold: "DB10.8.5",
    plcHeld: "DB10.8.6",
    errorCode: "DB10.12",
    heartbeat: "DB10.16",
    params,
    measured: { Temperature: "DB10.84", Pressure: "DB10.88", HoldTime: "DB10.92" },
    opcUaUseSecurity: false,
    opcUaAutoAcceptCertificates: false,
    opcUaUser: "",
    opcUaPassword: ""
  };
}

function applyMap(json: string) {
  const raw = JSON.parse(json || "{}") as Record<string, unknown>;
  const next = emptyMap();
  next.stepId = String(raw.stepId ?? raw.StepId ?? next.stepId);
  next.stepType = String(raw.stepType ?? raw.StepType ?? next.stepType);
  next.triggerWrite = String(raw.triggerWrite ?? raw.TriggerWrite ?? next.triggerWrite);
  next.plcReady = String(raw.plcReady ?? raw.PlcReady ?? next.plcReady);
  next.stepRunning = String(raw.stepRunning ?? raw.StepRunning ?? next.stepRunning);
  next.stepComplete = String(raw.stepComplete ?? raw.StepComplete ?? next.stepComplete);
  next.stepError = String(raw.stepError ?? raw.StepError ?? next.stepError);
  next.hostHold = String(raw.hostHold ?? raw.HostHold ?? next.hostHold);
  next.plcHeld = String(raw.plcHeld ?? raw.PlcHeld ?? next.plcHeld);
  next.errorCode = String(raw.errorCode ?? raw.ErrorCode ?? next.errorCode);
  next.heartbeat = String(raw.heartbeat ?? raw.Heartbeat ?? next.heartbeat);
  const measured = (raw.measured ?? raw.Measured ?? {}) as Record<string, string>;
  next.measured.Temperature = measured.Temperature ?? next.measured.Temperature;
  next.measured.HoldTime = measured.HoldTime ?? next.measured.HoldTime;
  next.measured.Pressure = measured.Pressure ?? next.measured.Pressure;
  const params = (raw.params ?? raw.Params) as string[] | undefined;
  if (Array.isArray(params) && params.length === 16) next.params = params;
  next.opcUaUseSecurity = Boolean(raw.opcUaUseSecurity ?? raw.OpcUaUseSecurity);
  next.opcUaAutoAcceptCertificates = Boolean(raw.opcUaAutoAcceptCertificates ?? raw.OpcUaAutoAcceptCertificates);
  next.opcUaUser = String(raw.opcUaUser ?? raw.OpcUaUser ?? "");
  next.opcUaPassword = String(raw.opcUaPassword ?? raw.OpcUaPassword ?? "");
  Object.assign(tagMap, next);
  tagMap.measured = next.measured;
  tagMap.params = next.params;
}

function applyWatchdog(json?: string | null) {
  const defaults = {
    readyWaitSeconds: 15,
    writeTimeoutSeconds: 5,
    ackTimeoutSeconds: 8,
    heartbeatTimeoutSeconds: 3,
    resetTimeoutSeconds: 8,
    idleSettleSeconds: 10,
    holdAckSeconds: 8
  };
  try {
    const raw = JSON.parse(json || "{}") as Record<string, number>;
    Object.assign(watchdog, defaults, raw);
  } catch {
    Object.assign(watchdog, defaults);
  }
}

async function load() {
  // 原先裸 await：4 秒轮询一旦失败会持续抛未处理 rejection，界面也不会有任何提示。
  await run(http.get<EquipmentDto[]>("/equipment"), (d) => (items.value = d));
  await run(http.get<EquipmentClassDto[]>("/equipment/classes"), (d) => (classes.value = d));
}

function openOccupant(row: EquipmentDto) {
  if (row.occupyingBatchId) router.push(`/batches/${row.occupyingBatchId}`);
}

function open(row?: EquipmentDto) {
  if (row) {
    Object.assign(form, row);
    applyMap(row.tagMapJson);
    applyWatchdog(row.watchdogJson);
  } else {
    Object.assign(form, {
      id: "", code: "", name: "", protocol: "Simulator", host: "127.0.0.1", port: 102,
      plcModel: "S7_1200", rack: 0, slot: 1, enabled: true, tagMapJson: "{}", description: "", watchdogJson: "",
      equipmentClassCode: ""
    });
    applyMap(JSON.stringify(emptyMap()));
    applyWatchdog(null);
  }
  visible.value = true;
}

async function save() {
  saving.value = true;
  try {
    form.tagMapJson = JSON.stringify({
      StepId: tagMap.stepId,
      StepType: tagMap.stepType,
      TriggerWrite: tagMap.triggerWrite,
      PlcReady: tagMap.plcReady,
      StepRunning: tagMap.stepRunning,
      StepComplete: tagMap.stepComplete,
      StepError: tagMap.stepError,
      HostHold: tagMap.hostHold,
      PlcHeld: tagMap.plcHeld,
      ErrorCode: tagMap.errorCode,
      Heartbeat: tagMap.heartbeat,
      Params: tagMap.params,
      Measured: tagMap.measured,
      OpcUaUseSecurity: tagMap.opcUaUseSecurity,
      OpcUaAutoAcceptCertificates: tagMap.opcUaAutoAcceptCertificates,
      OpcUaUser: tagMap.opcUaUser,
      OpcUaPassword: tagMap.opcUaPassword
    });
    form.watchdogJson = JSON.stringify(watchdog);
    if (form.id) await http.put(`/equipment/${form.id}`, form);
    else await http.post("/equipment", form);
    visible.value = false;
    await load();
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    saving.value = false;
  }
}

async function validate(row: EquipmentDto) {
  // 键里带动作：一行有三个操作按钮，只写 row.id 会让没被点的那两个也一起转。
  busy.value = `validate:${row.id}`;
  try {
    const { data } = await http.post<TagMapCheckDto>(`/equipment/${row.id}/validate-tagmap`);
    ElMessage.success(data.message);
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
    const { data } = await http.post<ConnectionTestDto>(`/equipment/${row.id}/test-connection`);
    if (data.connected) ElMessage.success(`${data.message} (${data.latencyMs.toFixed(0)} ms)`);
    else ElMessage.error(data.message);
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    busy.value = "";
  }
}

async function inject(row: EquipmentDto, mode: string) {
  busy.value = `inject:${row.id}`;
  try {
    const { data } = await http.post<TagMapCheckDto>(`/equipment/${row.id}/inject-fault`, { mode });
    ElMessage.warning(data.message);
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
</style>
