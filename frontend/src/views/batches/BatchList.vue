<template>
  <div>
    <div class="page-title">
      <h2>生产批次</h2>
      <div class="toolbar">
        <el-radio-group v-model="filter" size="small">
          <el-radio-button value="all">全部</el-radio-button>
          <el-radio-button value="Running">执行中</el-radio-button>
          <el-radio-button value="Queued">排队</el-radio-button>
          <el-radio-button value="Completed">待放行</el-radio-button>
          <el-radio-button value="Faulted">故障</el-radio-button>
          <el-radio-button value="Released">已放行</el-radio-button>
        </el-radio-group>
        <el-button v-if="auth.can('Operator', 'Supervisor')" type="primary" :loading="openingCreate" @click="openCreate">从已批准配方创建</el-button>
      </div>
    </div>
    <el-alert class="gap-after"
      v-if="loadError"
      :closable="false"
      type="error"
      :title="`批次列表加载失败：${loadError}`"
      show-icon
     
    />
    <el-table
      :data="shown"
      v-loading="initialLoading"
      :empty-text="filter === 'all' ? '暂无批次' : '当前筛选条件下没有批次'"
      class="clickable-rows"
      @row-click="(row: BatchListItemDto) => $router.push(`/batches/${row.id}`)"
    >
      <el-table-column prop="batchNo" label="批次号" width="160" fixed />
      <el-table-column prop="recipeName" label="控制配方" />
      <el-table-column prop="recipeVersion" label="版本" width="70" />
      <el-table-column prop="equipmentCode" label="主设备" width="100" />
      <el-table-column prop="status" label="状态" width="120">
        <template #default="{ row }">
          <el-tag size="small" :type="batchStatusTagType(row.status)" effect="dark">{{ batchStatusLabel(row.status) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="handshakePhase">
        <template #header><HelpTip term="四步握手" /></template>
        <template #default="{ row }">
          <HelpTip :term="handshakePhaseLabel(row.handshakePhase)">{{ handshakePhaseLabel(row.handshakePhase) }}</HelpTip>
        </template>
      </el-table-column>
    </el-table>
    <el-dialog v-model="visible" title="创建批次 / 生成控制配方快照" width="640px">
      <el-form label-width="120px">
        <el-form-item label="批次号"><el-input v-model="form.batchNo" /></el-form-item>
        <el-form-item label="主配方">
          <el-select v-model="form.recipeId" style="width:100%">
            <el-option v-for="r in recipes" :key="r.id" :label="`${r.code} ${r.name} (v${r.approvedVersion ?? '-'})`" :value="r.id" :disabled="!r.approvedVersion" />
          </el-select>
        </el-form-item>
        <el-form-item label="主设备">
          <el-select v-model="form.equipmentId" style="width:100%">
            <el-option
              v-for="e in equipment"
              :key="e.id"
              :label="equipmentLabel(e)"
              :value="e.id"
              :disabled="!e.enabled"
            />
          </el-select>
          <div v-if="selectedEquipment?.occupancy === 'Occupied'" class="unit-hint">
            {{ selectedEquipment.code }} 正被 {{ selectedEquipment.occupyingBatchNo }} 占用。可以先生成控制配方快照，启动执行须等设备空闲（禁止双批盲写）。
          </div>
        </el-form-item>
        <el-form-item v-if="selectedUnits.length > 1" label="单元设备">
          <div class="unit-hint">同一波次的 Unit Procedure 绑定不同设备时并行四步握手；同一设备仍串行。</div>
          <div v-for="unit in selectedUnits" :key="unit" class="unit-bind">
            <span>{{ unit }}</span>
            <el-select v-model="form.unitEquipment[unit]" style="width:280px">
              <el-option
                v-for="e in equipment"
                :key="e.id"
                :label="equipmentLabel(e)"
                :value="e.id"
                :disabled="!e.enabled"
              />
            </el-select>
          </div>
        </el-form-item>
        <el-form-item label="缩放因子">
          <el-input-number v-model="form.scaleFactor" :min="0.01" :max="100" :step="0.1" />
        </el-form-item>
        <el-form-item label="物料批次"><el-input v-model="form.lotNumber" placeholder="产出批号，写入控制配方快照（不纳入哈希）" /></el-form-item>
        <el-form-item label="投料批">
          <el-select v-model="form.chargeLotIds" multiple filterable placeholder="可选，来料/拆分子批" style="width:100%">
            <el-option
              v-for="lot in chargeLots"
              :key="lot.id"
              :label="`${lot.lotNumber} ${lot.materialCode} · ${lot.status}`"
              :value="lot.id"
            />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="visible = false">取消</el-button>
        <el-button type="primary" :loading="creating" @click="create">生成快照并创建</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import http from "../../api/http";
import type { BatchListItemDto, EquipmentDto, ExecutionEvent, MaterialLotDto, RecipeListItemDto } from "../../api/types";
import { applyOccupancyToEquipment, occupancyFromEvent, useExecutionHub } from "../../realtime/executionHub";
import { useCoalescedReload } from "../../utils/useCoalescedReload";
import { usePolling } from "../../utils/usePolling";
import { useAuthStore } from "../../stores/auth";
import { batchStatusLabel, batchStatusTagType, handshakePhaseLabel } from "../../utils/labels";
import HelpTip from "../../components/HelpTip.vue";

const auth = useAuthStore();
const route = useRoute();
const router = useRouter();

const items = ref<BatchListItemDto[]>([]);
const recipes = ref<RecipeListItemDto[]>([]);
const equipment = ref<EquipmentDto[]>([]);
const lots = ref<MaterialLotDto[]>([]);
const visible = ref(false);
const initialLoading = ref(true);
const loadError = ref("");
const openingCreate = ref(false);
const creating = ref(false);
// 轮询也必须走合并窗口：绕过 scheduleReload 会和事件驱动的重拉叠打出重复请求。
const poll = usePolling(() => scheduleReload());
const form = reactive({
  batchNo: "",
  recipeId: "",
  equipmentId: "",
  scaleFactor: 1,
  lotNumber: "",
  unitEquipment: {} as Record<string, string>,
  chargeLotIds: [] as string[]
});
const chargeLots = computed(() => lots.value.filter((l) => l.status === "Open" || l.status === "Released"));

const filter = computed({
  get: () => {
    const raw = route.query.status;
    const value = Array.isArray(raw) ? raw[0] : raw;
    return value && value !== "all" ? String(value) : "all";
  },
  set: (value: string) => {
    void router.replace({ path: "/batches", query: value === "all" ? {} : { status: value } });
  }
});
const shown = computed(() =>
  filter.value === "all" ? items.value : items.value.filter((row) => row.status === filter.value));

const selectedUnits = computed(() => {
  const recipe = recipes.value.find((r) => r.id === form.recipeId);
  return recipe?.unitProcedures?.filter(Boolean) ?? [];
});
const selectedEquipment = computed(() =>
  equipment.value.find((e) => e.id === form.equipmentId) ?? null);

function equipmentLabel(e: EquipmentDto) {
  const occ = e.occupancy === "Occupied" && e.occupyingBatchNo ? ` · 占用 ${e.occupyingBatchNo}` : " · 空闲";
  const klass = e.equipmentClassCode ? ` · ${e.equipmentClassCode}` : "";
  return `${e.code} ${e.name}${klass} · ${e.protocol}${occ}`;
}

function syncUnitBindings() {
  const extra = equipment.value.find((e) => e.id !== form.equipmentId);
  const next: Record<string, string> = {};
  for (const unit of selectedUnits.value)
    next[unit] = form.equipmentId;
  if (extra && selectedUnits.value.length > 1) {
    const peer = selectedUnits.value.find((u) => /淬火|quench|UP-B/i.test(u));
    if (peer) next[peer] = extra.id;
  }
  form.unitEquipment = next;
}

watch(() => [form.recipeId, form.equipmentId], syncUnitBindings);

async function load() {
  try {
    items.value = (await http.get<BatchListItemDto[]>("/batches")).data;
    loadError.value = "";
    if (visible.value)
      equipment.value = (await http.get<EquipmentDto[]>("/equipment")).data;
  } catch (e) {
    loadError.value = (e as Error).message || "批次列表加载失败";
  } finally {
    initialLoading.value = false;
  }
}

// 握手事件约 100ms 一条，而列表行状态只能靠重拉补齐——合并窗口避免把后端打满。
const scheduleReload = useCoalescedReload(load);

function onExecution(evt: ExecutionEvent) {
  const occupancy = occupancyFromEvent(evt);
  if (occupancy) {
    if (equipment.value.length) equipment.value = applyOccupancyToEquipment(equipment.value, occupancy);
    return;
  }
  if (evt.type === "sample") return;
  scheduleReload();
}

useExecutionHub({ onExecution });

async function openCreate() {
  openingCreate.value = true;
  try {
    // 三个取数原本串行 await，弹窗要等满三跳才打开；改并发并补错误处理（原先失败会静默无反应）。
    const [recipeRes, equipmentRes, lotRes] = await Promise.all([
      http.get<RecipeListItemDto[]>("/recipes"),
      http.get<EquipmentDto[]>("/equipment"),
      http.get<MaterialLotDto[]>("/lots")
    ]);
    recipes.value = recipeRes.data;
    equipment.value = equipmentRes.data;
    lots.value = lotRes.data;
    form.batchNo = `B${new Date().toISOString().slice(0, 19).replace(/[-:T]/g, "")}`;
    form.recipeId = recipes.value.find((r) => r.approvedVersion)?.id ?? "";
    form.equipmentId = equipment.value[0]?.id ?? "";
    form.scaleFactor = 1;
    form.lotNumber = "";
    form.chargeLotIds = [];
    syncUnitBindings();
    visible.value = true;
  } catch (e) {
    ElMessage.error(`无法打开创建窗口：${(e as Error).message}`);
  } finally {
    openingCreate.value = false;
  }
}

async function create() {
  // 原先缺前置校验，空选就提交只能拿到后端报错，这里提前拦住。
  if (!form.recipeId) return void ElMessage.warning("请先选择一个已批准的主配方。");
  if (!form.equipmentId) return void ElMessage.warning("请选择主设备。");
  creating.value = true;
  try {
    const payload = {
      batchNo: form.batchNo,
      recipeId: form.recipeId,
      equipmentId: form.equipmentId,
      scaleFactor: form.scaleFactor,
      lotNumber: form.lotNumber || null,
      unitEquipment: selectedUnits.value.length > 1 ? { ...form.unitEquipment } : null,
      chargeLotIds: form.chargeLotIds.length ? form.chargeLotIds : null
    };
    const { data } = await http.post("/batches", payload);
    visible.value = false;
    await router.push(`/batches/${data.id}`);
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    creating.value = false;
  }
}

onMounted(async () => {
  await load();
  poll.start();
});
</script>

<style scoped>
.toolbar { display: flex; align-items: center; gap: var(--space-3); flex-wrap: wrap; }
.unit-hint { color: var(--muted); font-size: 12px; margin-bottom: var(--space-2); line-height: 1.4; }
.unit-bind { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); margin-bottom: var(--space-2); }
</style>
