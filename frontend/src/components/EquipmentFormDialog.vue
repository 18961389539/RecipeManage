<template>
  <el-dialog v-model="visible" :title="form.id ? $t('设备 / 握手点表') : $t('新增设备')" width="720px">
    <p class="param-hint">
      {{ $t("写参槽 {0} … {1}。", [tagMap.params[0] || "—", tagMap.params[PARAM_SLOTS - 1] || "—"]) }}
      <el-button link type="primary" @click="activeTab = 'params'">{{ $t("核对地址") }}</el-button>
    </p>
    <el-form label-width="110px" :disabled="!canEdit">
      <el-tabs v-model="activeTab" class="equip-form-tabs">
        <el-tab-pane :label="$t('基本与超时')" name="basic">
          <el-form-item :label="$t('编码')"><el-input v-model="form.code" :disabled="!!form.id" /></el-form-item>
          <el-form-item :label="$t('名称')"><el-input v-model="form.name" /></el-form-item>
          <el-form-item :label="$t('协议')">
            <el-select v-model="form.protocol" style="width:100%">
              <el-option v-for="p in protocols" :key="p" :label="protocolLabel(p)" :value="p" />
            </el-select>
          </el-form-item>
          <el-form-item :label="$t('主机')"><el-input v-model="form.host" :placeholder="$t('IP 或 opc.tcp://host:4840')" /></el-form-item>
          <el-form-item :label="$t('端口')"><el-input-number v-model="form.port" /></el-form-item>
          <el-form-item :label="$t('型号')"><el-input v-model="form.plcModel" /></el-form-item>
          <el-form-item :label="$t('机架 / 插槽')">
            <el-input-number v-model="form.rack" /> / <el-input-number v-model="form.slot" />
          </el-form-item>
          <el-form-item :label="$t('启用')"><el-switch v-model="form.enabled" /></el-form-item>
          <el-form-item :label="$t('设备类')">
            <el-select v-model="form.equipmentClassCode" clearable :placeholder="$t('未分类则跳过相能力校验')" style="width:100%">
              <el-option v-for="c in classes" :key="c.code" :label="`${c.code} · ${c.name}`" :value="c.code" />
            </el-select>
          </el-form-item>
          <el-divider>{{ $t("握手看门狗（秒）") }}</el-divider>
          <el-form-item :label="$t('等待 Ready')"><el-input-number v-model="watchdog.readyWaitSeconds" :min="2" /></el-form-item>
          <el-form-item :label="$t('写参超时')"><el-input-number v-model="watchdog.writeTimeoutSeconds" :min="1" /></el-form-item>
          <el-form-item :label="$t('应答超时')"><el-input-number v-model="watchdog.ackTimeoutSeconds" :min="1" /></el-form-item>
          <el-form-item :label="$t('心跳超时')"><el-input-number v-model="watchdog.heartbeatTimeoutSeconds" :min="1" /></el-form-item>
          <el-form-item :label="$t('复位超时')"><el-input-number v-model="watchdog.resetTimeoutSeconds" :min="1" /></el-form-item>
          <el-form-item :label="$t('保持应答')"><el-input-number v-model="watchdog.holdAckSeconds" :min="1" /></el-form-item>
          <el-form-item>
            <template #label><HelpTip term="空闲安定" /></template>
            <el-input-number v-model="watchdog.idleSettleSeconds" :min="1" />
          </el-form-item>
        </el-tab-pane>
        <el-tab-pane :label="$t('握手点表')" name="tags">
          <el-form-item v-for="tag in tagFields" :key="tag.key">
            <template #label><HelpTip :term="tag.term">{{ tag.term }}</HelpTip></template>
            <el-input v-model="tagMap[tag.key]" />
          </el-form-item>
          <el-divider>{{ $t("实测点") }}</el-divider>
          <p class="param-hint">
            <HelpTip term="实测点">{{ $t("配方参数按这里的键名读实测值归档") }}</HelpTip>{{ $t("。 键名要与配方里填的一致；粘度、流量、计数这类非热工量自己加行，不要挤进 Temperature。") }}
          </p>
          <div v-if="tagMap.measured.length" class="measured-list">
            <template v-for="(m, i) in tagMap.measured" :key="i">
              <el-input v-model="m.name" :placeholder="$t('键名，如 Viscosity')" />
              <el-input v-model="m.address" :placeholder="$t('地址，如 DB40.0')" />
              <el-button link type="danger" @click="removeMeasured(i)">{{ $t("删除") }}</el-button>
            </template>
          </div>
          <p v-else class="param-hint">{{ $t("这台设备没有实测点。配方里声明了实测点的参数会在开批时被拦下。") }}</p>
          <el-button class="gap-before-sm" size="small" @click="addMeasured">{{ $t("添加实测点") }}</el-button>
          <template v-if="form.protocol === 'OpcUa'">
            <el-divider>{{ $t("OPC UA 安全（默认不自动接受证书、匿名）") }}</el-divider>
            <el-form-item :label="$t('签名端点')"><el-switch v-model="tagMap.opcUaUseSecurity" /></el-form-item>
            <el-form-item :label="$t('接受自签证书')"><el-switch v-model="tagMap.opcUaAutoAcceptCertificates" /></el-form-item>
            <el-form-item :label="$t('用户名')"><el-input v-model="tagMap.opcUaUser" :placeholder="$t('留空为匿名')" /></el-form-item>
            <el-form-item :label="$t('密码')"><el-input v-model="tagMap.opcUaPassword" type="password" show-password /></el-form-item>
          </template>
        </el-tab-pane>
        <el-tab-pane name="params">
          <template #label><HelpTip term="写参槽">{{ $t("写参槽") }}</HelpTip></template>
          <p class="param-hint">{{ $t("工步设定值按槽位写入这些地址。默认 DB10.20 起每 4 字节一槽，与现场点表不符时必须改，否则写参会落到错误偏移。") }}</p>
          <div class="param-grid">
            <el-form-item v-for="i in PARAM_SLOTS" :key="i" :label="`Param[${i - 1}]`">
              <el-input v-model="tagMap.params[i - 1]" />
            </el-form-item>
          </div>
        </el-tab-pane>
      </el-tabs>
    </el-form>
    <template #footer>
      <el-button @click="visible = false">{{ $t("关闭") }}</el-button>
      <el-button v-if="canEdit" type="primary" :loading="saving" @click="save">{{ $t("保存") }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { reactive, ref, watch } from "vue";
import { ElMessage } from "element-plus";
import { saveEquipment } from "../api/equipment";
import type { EquipmentClassDto, EquipmentDto, PlcProtocol } from "../api/types";
import { protocolLabel } from "../utils/labels";
import HelpTip from "./HelpTip.vue";
import {
  DEFAULT_WATCHDOG, PARAM_SLOTS, emptyTagMap, parseTagMap, parseWatchdog, serializeTagMap, serializeWatchdog
} from "../utils/tagMap";
import type { TagMapForm, WatchdogForm } from "../utils/tagMap";

/**
 * 设备与握手点表表单。
 *
 * 从设备页抽出来的原因：它连同点表/看门狗的读写有 300+ 行，把设备列表本身挤到看不见。
 * 点表的 JSON 翻译不在这里做，一律走 utils/tagMap（那份是唯一可以单测的翻译点）。
 *
 * 表单只在对话框打开时从 `equipment` 灌一次值：列表每 4 秒会重拉，
 * 如果持续跟随 props 同步，用户正在敲的地址会被轮询结果覆盖掉。
 */
const visible = defineModel<boolean>({ required: true });
const props = withDefaults(defineProps<{
  equipment?: EquipmentDto | null;
  classes?: EquipmentClassDto[];
  canEdit?: boolean;
}>(), { equipment: null, classes: () => [], canEdit: false });
const emit = defineEmits<{ saved: [] }>();

const protocols: PlcProtocol[] = ["Simulator", "SiemensS7", "ModbusTcp", "OpcUa"];
// 点表里 11 个同构的字符串地址项：列成表渲染，避免 11 段只差一个键名的模板。
const tagFields = [
  { key: "stepId", term: "Step_ID" },
  { key: "stepType", term: "Step_Type" },
  { key: "triggerWrite", term: "Trigger_Write" },
  { key: "plcReady", term: "PLC_Ready" },
  { key: "stepRunning", term: "Step_Running" },
  { key: "stepComplete", term: "Step_Complete" },
  { key: "stepError", term: "Step_Error" },
  { key: "hostHold", term: "Host_Hold" },
  { key: "plcHeld", term: "PLC_Held" },
  { key: "errorCode", term: "Error_Code" },
  { key: "heartbeat", term: "Heartbeat" }
] as const;

const activeTab = ref("basic");
const saving = ref(false);
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
  description: "",
  equipmentClassCode: ""
});
const tagMap = reactive<TagMapForm>(emptyTagMap());
const watchdog = reactive<WatchdogForm>({ ...DEFAULT_WATCHDOG });

watch(visible, (open) => {
  if (!open) return;
  const row = props.equipment;
  Object.assign(form, row
    ? {
        id: row.id, code: row.code, name: row.name, protocol: row.protocol, host: row.host, port: row.port,
        plcModel: row.plcModel, rack: row.rack, slot: row.slot, enabled: row.enabled,
        description: row.description ?? "", equipmentClassCode: row.equipmentClassCode ?? ""
      }
    : {
        id: "", code: "", name: "", protocol: "Simulator", host: "127.0.0.1", port: 102,
        plcModel: "S7_1200", rack: 0, slot: 1, enabled: true, description: "", equipmentClassCode: ""
      });
  Object.assign(tagMap, row ? parseTagMap(row.tagMapJson) : emptyTagMap());
  Object.assign(watchdog, parseWatchdog(row?.watchdogJson));
  activeTab.value = "basic";
});

function addMeasured() {
  tagMap.measured.push({ name: "", address: "" });
}

function removeMeasured(index: number) {
  tagMap.measured.splice(index, 1);
}

async function save() {
  saving.value = true;
  try {
    await saveEquipment(form.id || null, {
      code: form.code,
      name: form.name,
      protocol: form.protocol,
      host: form.host,
      port: form.port,
      plcModel: form.plcModel,
      rack: form.rack,
      slot: form.slot,
      enabled: form.enabled,
      tagMapJson: serializeTagMap(tagMap),
      description: form.description,
      watchdogJson: serializeWatchdog(watchdog),
      equipmentClassCode: form.equipmentClassCode || null
    });
    visible.value = false;
    emit("saved");
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    saving.value = false;
  }
}
</script>

<style scoped>
.param-hint { color: var(--muted); font-size: 12px; margin: 0 0 var(--space-2); }
.param-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0 var(--space-3);
  margin-bottom: var(--space-2);
}
.param-grid :deep(.el-form-item) { margin-bottom: var(--space-2); }
.measured-list {
  display: grid;
  grid-template-columns: 1fr 1fr auto;
  gap: var(--space-2);
  align-items: center;
}
.equip-form-tabs :deep(.el-tabs__header) { margin-bottom: var(--space-3); }
</style>
