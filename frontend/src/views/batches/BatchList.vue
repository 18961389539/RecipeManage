<template>
  <div>
    <div class="page-title">
      <div>
        <h2>{{ $t("生产批次") }}<PageGuideButton guide-key="batches" /></h2>
        <span>{{ $t("从已批准主配方生成控制配方，并跟踪执行到放行。") }}</span>
      </div>
      <div>
        <el-button v-if="auth.can('Operator', 'Supervisor')" type="primary" :loading="openingCreate" @click="openCreate">{{ $t("从已批准配方创建") }}</el-button>
      </div>
    </div>
    <div class="filter-bar">
      <HelpTip term="聚焦本页搜索" chord="/" plain placement="bottom">
        <el-input
          v-model="query"
          class="search-field"
          clearable
          data-shortcut-search
          :placeholder="$t('搜索批次号 / 配方 / 设备')"
        />
      </HelpTip>
      <span v-if="!initialLoading" class="result-count">{{ countText }}</span>
      <el-radio-group v-model="filter" size="small" class="filter-chips">
        <el-radio-button value="all">{{ $t("全部") }}</el-radio-button>
        <el-radio-button value="Created">{{ $t("已创建") }}</el-radio-button>
        <el-radio-button value="Queued">{{ $t("排队") }}</el-radio-button>
        <el-radio-button value="Running">{{ $t("执行中") }}</el-radio-button>
        <el-radio-button value="Held">{{ $t("保持") }}</el-radio-button>
        <el-radio-button value="Completed">{{ $t("待放行") }}</el-radio-button>
        <el-radio-button value="lab-pending">{{ $t("待检终样") }}</el-radio-button>
        <el-radio-button value="Faulted">{{ $t("故障") }}</el-radio-button>
        <el-radio-button value="Released">{{ $t("已放行") }}</el-radio-button>
        <el-radio-button value="DispositionRejected">{{ $t("已拒收") }}</el-radio-button>
        <el-radio-button value="Aborted">{{ $t("已中止") }}</el-radio-button>
      </el-radio-group>
    </div>
    <el-alert class="gap-after"
      v-if="loadError"
      :closable="false"
      type="error"
      :title="$t('批次列表加载失败：{0}', [loadError])"
      show-icon
     
    />
    <el-alert class="gap-after"
      v-if="labPending && !loadError"
      :closable="false"
      type="info"
      show-icon
      :title="$t('仅显示有待检终样的批次。打开电子批记录判定样品。')"
    />
    <el-table
      ref="tableRef"
      :data="items"
      v-loading="initialLoading"
      :empty-text="emptyText"
      :row-class-name="batchRowClass"
      class="clickable-rows batch-table"
      scrollbar-always-on
      max-height="calc(100vh - 292px)"
      :default-sort="defaultSort"
      @sort-change="onSortChange"
      @row-click="(row: BatchListItemDto) => $router.push(`/batches/${row.id}`)"
    >
      <el-table-column prop="batchNo" :label="$t('批次号')" width="148" fixed sortable="custom" :sort-orders="SERVER_ASC_FIRST" show-overflow-tooltip />
      <el-table-column prop="recipeName" :label="$t('控制配方')" min-width="112" sortable="custom" :sort-orders="SERVER_ASC_FIRST" show-overflow-tooltip />
      <el-table-column prop="recipeVersion" :label="$t('版本')" width="76" sortable="custom" :sort-orders="SERVER_ASC_FIRST">
        <template #default="{ row }">
          <span v-if="row.recipeVersion">v{{ row.recipeVersion }}</span>
          <span v-else>—</span>
        </template>
      </el-table-column>
      <el-table-column prop="equipmentCode" :label="$t('主设备')" width="96" sortable="custom" :sort-orders="SERVER_ASC_FIRST" />
      <el-table-column prop="status" :label="$t('状态')" width="96" sortable="custom" :sort-orders="SERVER_ASC_FIRST">
        <template #default="{ row }">
          <div class="status-pills">
            <el-tag size="small" :type="batchStatusTagType(row.status)" effect="dark">{{ batchStatusLabel(row.status) }}</el-tag>
            <!-- 两个标签并排会被读成同一种"状态"：悬停解释第二个是"待检终样"待办，不是批次状态。 -->
            <HelpTip v-if="row.pendingFinalSample" term="待检终样" plain placement="bottom">
              <el-tag size="small" type="warning" effect="plain">{{ $t("待检终样") }}</el-tag>
            </HelpTip>
          </div>
        </template>
      </el-table-column>
      <el-table-column prop="handshakePhase" width="128" show-overflow-tooltip>
        <template #header><HelpTip term="四步握手" /></template>
        <template #default="{ row }">
          <HelpTip :term="handshakePhaseTip(handshakeDisplayPhase(row.status, null, row.handshakePhase))">{{ handshakePhaseLabel(handshakeDisplayPhase(row.status, null, row.handshakePhase)) }}</HelpTip>
        </template>
      </el-table-column>
      <el-table-column prop="createdAt" :label="$t('创建时间')" width="176" sortable="custom" :sort-orders="SERVER_DESC_FIRST" show-overflow-tooltip>
        <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
      </el-table-column>
    </el-table>
    <el-pagination class="pager" layout="total, prev, pager, next" background small :page-size="take"
      :current-page="page" :total="total" hide-on-single-page @current-change="onPageChange" />
    <!-- 空库时给新装机指路：批次的唯一来源是"已批准主配方"，比"暂无批次"四个字有用得多。
         取数失败时不出现——那时"还没有批次"是假信息，用户该去看上面的错误提示。 -->
    <div v-if="!initialLoading && !loadError && !items.length && !filtered" class="muted gap-before">
      {{ $t("还没有批次：先在「主配方设计」把一份配方提交审核，批准后即可在这里创建生产批次。") }}
    </div>
    <el-dialog v-model="visible" :title="$t('创建批次 / 生成控制配方快照')" width="640px">
      <el-form ref="formRef" :model="form" :rules="rules" label-width="120px" @submit.prevent="create">
        <el-form-item :label="$t('批次号')" prop="batchNo"><el-input v-model="form.batchNo" /></el-form-item>
        <el-form-item :label="$t('主配方')" prop="recipeId">
          <el-select v-model="form.recipeId" filterable style="width:100%" :placeholder="$t('选择已批准主配方')" :no-data-text="$t('没有已批准的主配方')">
            <el-option v-for="r in approvedRecipes" :key="r.id" :label="`${r.code} ${r.name} (v${r.approvedVersion})`" :value="r.id" />
          </el-select>
          <div v-if="!approvedRecipes.length" class="unit-hint">{{ $t("没有已批准的主配方。草稿不会出现在此列表，请先走审核批准。") }}</div>
        </el-form-item>
        <el-form-item prop="equipmentId">
          <template #label><HelpTip term="主设备">{{ $t("主设备") }}</HelpTip></template>
          <el-select v-model="form.equipmentId" filterable style="width:100%" :placeholder="$t('选择主设备')">
            <el-option
              v-for="e in equipment"
              :key="e.id"
              :label="equipmentOptionLabel(e)"
              :value="e.id"
              :disabled="!!primaryIncompatibility(e)"
            />
          </el-select>
          <div v-if="selectedEquipment && primaryIncompatibility(selectedEquipment)" class="unit-hint">
            {{ primaryIncompatibility(selectedEquipment) }}
          </div>
          <div v-else-if="selectedEquipment?.occupancy === 'Occupied'" class="unit-hint">
            {{ $t("{0} 正被 {1} 占用。可以先生成控制配方快照，启动执行须等设备空闲（禁止双批盲写）。", [selectedEquipment.code, selectedEquipment.occupyingBatchNo]) }}
          </div>
        </el-form-item>
        <el-form-item v-if="selectedUnits.length > 1">
          <template #label><HelpTip term="单元设备">{{ $t("单元设备") }}</HelpTip></template>
          <div class="unit-hint">{{ $t("同一波次的单元规程绑定不同设备时并行四步握手；同一设备仍串行。") }}</div>
          <div v-for="unit in selectedUnits" :key="unit" class="unit-bind">
            <span>{{ unit }}</span>
            <el-select v-model="form.unitEquipment[unit]" filterable style="width:280px">
              <el-option
                v-for="e in equipment"
                :key="e.id"
                :label="equipmentOptionLabel(e, unit)"
                :value="e.id"
                :disabled="!!unitIncompatibility(e, unit)"
              />
            </el-select>
          </div>
          <div v-if="sharedEquipment.length" class="unit-hint">
            {{ sharedEquipment.map((s) => `${s.code}：${s.units.join(' / ')}`).join('；') }}
            {{ $t("共用同一台设备，这一波会退回串行执行（一台设备同时只允许一个四步握手）。") }}
          </div>
        </el-form-item>
        <el-form-item :label="$t('缩放因子')">
          <el-input-number v-model="form.scaleFactor" :min="0.01" :max="100" :step="0.1" />
        </el-form-item>
        <el-form-item :label="$t('物料批次')"><el-input v-model="form.lotNumber" :placeholder="$t('产出批号，写入控制配方快照（不纳入哈希）')" /></el-form-item>
        <el-form-item :label="$t('投料批')">
          <el-select v-model="form.chargeLotIds" multiple filterable :placeholder="$t('可选，来料/拆分子批')" style="width:100%">
            <el-option
              v-for="lot in lots"
              :key="lot.id"
              :label="`${lot.lotNumber} ${lot.materialCode} · ${lotStatusLabel(lot.status)}`"
              :value="lot.id"
            />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="visible = false">{{ $t("取消") }}</el-button>
        <el-button type="primary" :loading="creating" @click="create">{{ $t("生成快照并创建") }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { t } from "../../i18n";
import { computed, nextTick, onMounted, reactive, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import type { FormInstance, FormRules } from "element-plus";
import type { TableInstance } from "element-plus";
import http from "../../api/http";
import type { BatchListItemDto, BatchListPageDto, EquipmentClassDto, EquipmentDto, ExecutionEvent, MaterialLotDto, MaterialLotPageDto, RecipeDetailDto, RecipeListItemDto, StepDto } from "../../api/types";
import { applyOccupancyToEquipment, occupancyFromEvent, useExecutionHub } from "../../realtime/executionHub";
import { useCoalescedReload } from "../../utils/useCoalescedReload";
import { usePolling } from "../../utils/usePolling";
import { useKeyboardRows } from "../../utils/useKeyboardRows";
import { SERVER_ASC_FIRST, SERVER_DESC_FIRST } from "../../utils/tableSort";
import { useServerPaging } from "../../utils/useServerPaging";
import { useAuthStore } from "../../stores/auth";
import { batchStatusLabel, batchStatusTagType, handshakePhaseLabel, handshakePhaseTip, lotStatusLabel, protocolLabel } from "../../utils/labels";
import { formatCompactStamp, formatDateTime } from "../../utils/format";
import HelpTip from "../../components/HelpTip.vue";
import { allowedProgramsByClass, classByUnit, classConflicts, programsByUnit, programsRejectedByClass } from "../../utils/plcProgram";
import { handshakeDisplayPhase } from "../../utils/handshake";
import { collectLanes } from "../../procedureLayout";

const auth = useAuthStore();
const route = useRoute();
const router = useRouter();

const items = ref<BatchListItemDto[]>([]);
const tableRef = ref<TableInstance>();
const recipes = ref<RecipeListItemDto[]>([]);
const equipment = ref<EquipmentDto[]>([]);
const lots = ref<MaterialLotDto[]>([]);
const phaseClasses = ref<EquipmentClassDto[]>([]);
const approvedSteps = ref<StepDto[]>([]);
const approvedEdges = ref<{ fromStepId: string; toStepId: string }[]>([]);
const visible = ref(false);
const initialLoading = ref(true);
const loadError = ref("");
const openingCreate = ref(false);
const creating = ref(false);
const query = ref("");
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
/** 可投料的来料批：状态筛选交给服务端，客户端过滤当页会漏掉别的 Open/Released 批。 */
const chargeLotParams = { take: 200, status: "Open,Released" };
const approvedRecipes = computed(() => recipes.value.filter((r) => r.approvedVersion));

const formRef = ref<FormInstance>();
/** 缺字段过去只在点「生成快照并创建」后弹 toast，现在填的时候就地说。
 *  重复批次号只是当页的前置检查（分页后手上没有全量），真正的判重仍是后端 DUP_BATCH。 */
function duplicateBatchNo(_rule: unknown, value: unknown, done: (error?: Error) => void) {
  const no = String(value ?? "").trim();
  done(no && items.value.some((b) => b.batchNo === no) ? new Error("批次号已存在，请换一个。") : undefined);
}
const rules = computed<FormRules<typeof form>>(() => ({
  batchNo: [
    { required: true, message: "请填写批次号。", trigger: "blur" },
    { validator: duplicateBatchNo, trigger: "blur" }
  ],
  recipeId: [{ required: true, message: "请选择已批准的主配方。", trigger: "change" }],
  equipmentId: [{ required: true, message: "请选择主设备。", trigger: "change" }]
}));

const labPending = computed(() => {
  const raw = route.query.lab;
  const value = Array.isArray(raw) ? raw[0] : raw;
  return value === "pending";
});

const filter = computed({
  get: () => {
    if (labPending.value) return "lab-pending";
    const raw = route.query.status;
    const value = Array.isArray(raw) ? raw[0] : raw;
    return value && value !== "all" ? String(value) : "all";
  },
  set: (value: string) => {
    if (value === "lab-pending") {
      void router.replace({ path: "/batches", query: { lab: "pending" } });
      return;
    }
    void router.replace({ path: "/batches", query: value === "all" ? {} : { status: value } });
  }
});
/** 搜索、筛选、排序、分页全在服务端：客户端只能看当页那 50 条，"共 N 条"会说谎。 */
const filtered = computed(() => !!query.value.trim() || filter.value !== "all" || labPending.value);
const pg = useServerPaging({ reload: load, defaultSort: "createdAt", search: query });
const { take, total, page, defaultSort, onSortChange, onPageChange } = pg;
const countText = computed(() => {
  const n = items.value.length;
  return filtered.value ? t("{0} / {1} 条", n, total.value) : t("共 {0} 条", total.value);
});
const emptyText = computed(() => {
  if (query.value.trim()) return "没有匹配的批次";
  if (labPending.value) return "没有待检终样的批次";
  return filter.value === "all" ? "暂无批次" : "当前筛选条件下没有批次";
});

// 整行可点，但 EP 渲染的 tr 不可聚焦——键盘用户此前打不开任何批次。
useKeyboardRows(tableRef, () => items.value);

const selectedUnits = computed(() => {
  if (approvedSteps.value.length)
    return collectLanes(approvedSteps.value);
  const recipe = recipes.value.find((r) => r.id === form.recipeId);
  return recipe?.unitProcedures?.filter(Boolean) ?? [];
});
const selectedEquipment = computed(() =>
  equipment.value.find((e) => e.id === form.equipmentId) ?? null);

/** 多个单元规程绑到同一台设备 = 这一波退回串行（一台设备同时只允许一个握手）。自动选设备时很容易撞在一起，界面上一句话不说就没人会发现。 */
const sharedEquipment = computed(() => {
  if (selectedUnits.value.length < 2) return [];
  const byEquipment = new Map<string, string[]>();
  for (const unit of selectedUnits.value) {
    const id = form.unitEquipment[unit];
    if (id) byEquipment.set(id, [...(byEquipment.get(id) ?? []), unit]);
  }
  return [...byEquipment.entries()]
    .filter(([, units]) => units.length > 1)
    .map(([id, units]) => ({
      code: equipment.value.find((e) => e.id === id)?.code ?? id,
      units
    }));
});
const allowedByClass = computed(() => allowedProgramsByClass(phaseClasses.value));
const unitProgramMap = computed(() => programsByUnit(approvedSteps.value));
const unitClassMap = computed(() => classByUnit(approvedSteps.value, approvedEdges.value));

function programsForUnit(unit: string) {
  return unitProgramMap.value.get(unit) ?? [];
}

function programsForPrimary() {
  const set = new Set<number>();
  for (const unit of selectedUnits.value)
    for (const program of programsForUnit(unit)) set.add(program);
  return [...set];
}

function classRejectReason(e: EquipmentDto, programs: number[], declaredClass?: string) {
  if (!e.enabled) return t("未启用");
  const rejected = programsRejectedByClass(e.equipmentClassCode, programs, allowedByClass.value);
  if (rejected.length) return t("类 {0} 不允许程序 {1}", e.equipmentClassCode, rejected.join("、"));
  if (classConflicts(declaredClass, e.equipmentClassCode))
    return t("单元声明 {0}，设备属于 {1}", declaredClass, e.equipmentClassCode);
  return "";
}

function primaryIncompatibility(e: EquipmentDto) {
  return classRejectReason(e, programsForPrimary(), primaryPreferClass());
}

function unitIncompatibility(e: EquipmentDto, unit: string) {
  return classRejectReason(e, programsForUnit(unit), unitClassMap.value.get(unit));
}

/**
 * 下拉宽度有限，越靠左的越容易被看到，所以按"决定这台能不能选"排序：
 * 编码 → 不可选原因 → 空闲/占用 → 名称与设备类 → 协议。
 * 原先占用状态拼在最末尾，280px 的下拉里刚好被裁掉（实测显示成「PR-01 搅拌加压单元 · PROCESS · 仿…」），
 * 而"现在能不能启动"恰恰是选设备时唯一要看的事实；不可选原因同理。
 */
function equipmentLabel(e: EquipmentDto, reason = "") {
  const occ = e.occupancy === "Occupied" && e.occupyingBatchNo ? t("占用 {0}", e.occupyingBatchNo) : t("空闲");
  const klass = e.equipmentClassCode ? ` · ${e.equipmentClassCode}` : "";
  return [e.code, reason, occ, `${e.name}${klass}`, protocolLabel(e.protocol)]
    .filter(Boolean)
    .join(" · ");
}

function equipmentOptionLabel(e: EquipmentDto, unit?: string) {
  return equipmentLabel(e, unit ? unitIncompatibility(e, unit) : primaryIncompatibility(e));
}

function primaryPreferClass() {
  for (const unit of selectedUnits.value) {
    const code = unitClassMap.value.get(unit);
    if (code) return code;
  }
  return undefined;
}

function firstCompatibleId(programs: number[], prefer?: string, preferClass?: string) {
  const compatible = equipment.value.filter((e) => !classRejectReason(e, programs, preferClass));
  const want = preferClass?.trim().toUpperCase();
  if (want) {
    const classHit = compatible.find((e) => e.equipmentClassCode?.toUpperCase() === want);
    if (classHit) return classHit.id;
  }
  const preferred = compatible.find((e) => e.id === prefer);
  if (preferred) return preferred.id;
  return compatible[0]?.id ?? prefer ?? "";
}

function syncUnitBindings() {
  const extra = equipment.value.find((e) => e.id !== form.equipmentId);
  const next: Record<string, string> = {};
  for (const unit of selectedUnits.value)
    next[unit] = firstCompatibleId(programsForUnit(unit), form.equipmentId, unitClassMap.value.get(unit));
  if (extra && selectedUnits.value.length > 1) {
    const peer = selectedUnits.value.find((u) => /淬火|quench|UP-B/i.test(u));
    if (peer && !unitIncompatibility(extra, peer)) next[peer] = extra.id;
  }
  form.unitEquipment = next;
}

async function loadApprovedSteps(recipeId: string) {
  approvedSteps.value = [];
  approvedEdges.value = [];
  if (!recipeId) return;
  const detail = (await http.get<RecipeDetailDto>(`/recipes/${recipeId}`)).data;
  approvedSteps.value = detail.approved?.steps ?? [];
  approvedEdges.value = (detail.approved?.edges ?? []).map((e) => ({ fromStepId: e.fromStepId, toStepId: e.toStepId }));
}

watch(() => form.recipeId, async (recipeId) => {
  if (!visible.value) return;
  try {
    await loadApprovedSteps(recipeId);
  } catch (e) {
    ElMessage.error((e as Error).message);
  }
  form.equipmentId = firstCompatibleId(
    programsForPrimary(),
    form.equipmentId,
    primaryPreferClass()
  );
  syncUnitBindings();
});

watch(() => form.equipmentId, () => {
  if (!visible.value) return;
  syncUnitBindings();
});

async function load() {
  try {
    const params = pg.params({
      status: labPending.value || filter.value === "all" ? undefined : filter.value,
      onlyLabPending: labPending.value ? "true" : undefined
    });
    const { data } = await http.get<BatchListPageDto>("/batches", { params });
    items.value = data.items;
    total.value = data.total;
    loadError.value = "";
    if (visible.value)
      equipment.value = (await http.get<EquipmentDto[]>("/equipment")).data;
  } catch (e) {
    loadError.value = (e as Error).message || "批次列表加载失败";
  } finally {
    initialLoading.value = false;
  }
}

/** 筛选 chips 走的是路由 query，服务端取数就得跟着路由重拉（以前 filter 只是过滤已加载的行）。 */
watch(() => [route.query.status, route.query.lab], () => pg.onFilterChange());

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
    // 取数并发；失败要提示（原先失败会静默无反应）。
    const [recipeRes, equipmentRes, lotRes, classRes] = await Promise.all([
      http.get<RecipeListItemDto[]>("/recipes"),
      http.get<EquipmentDto[]>("/equipment"),
      http.get<MaterialLotPageDto>("/lots", { params: chargeLotParams }),
      http.get<EquipmentClassDto[]>("/equipment/classes")
    ]);
    recipes.value = recipeRes.data;
    equipment.value = equipmentRes.data;
    lots.value = lotRes.data.items;
    phaseClasses.value = classRes.data;
    form.batchNo = `B${formatCompactStamp()}`;
    form.recipeId = recipes.value.find((r) => r.approvedVersion)?.id ?? "";
    form.scaleFactor = 1;
    form.lotNumber = "";
    form.chargeLotIds = [];
    await loadApprovedSteps(form.recipeId);
    form.equipmentId = firstCompatibleId(
      programsForPrimary(),
      equipment.value[0]?.id,
      primaryPreferClass()
    );
    syncUnitBindings();
    visible.value = true;
    // 重开窗口要清掉上一次的红字，否则残留的报错会挂在已经改对的字段上。
    void nextTick(() => formRef.value?.clearValidate());
  } catch (e) {
    ElMessage.error(t("无法打开创建窗口：{0}", (e as Error).message));
  } finally {
    openingCreate.value = false;
  }
}

async function create() {
  // 缺字段交给就地校验；这里只拦"后端也算不出来"的兼容性条件。
  if (!(await formRef.value?.validate().catch(() => false))) return;
  const primary = selectedEquipment.value;
  if (!primary) return void ElMessage.warning(t("请选择主设备。"));
  const primaryReason = primaryIncompatibility(primary);
  if (primaryReason) return void ElMessage.warning(t("主设备不兼容：{0}", primaryReason));
  if (selectedUnits.value.length > 1) {
    for (const unit of selectedUnits.value) {
      const bound = equipment.value.find((e) => e.id === form.unitEquipment[unit]);
      if (!bound) return void ElMessage.warning(t("{0} 未绑定设备。", unit));
      const reason = unitIncompatibility(bound, unit);
      if (reason) return void ElMessage.warning(t("{0} 绑定的设备不兼容：{1}", unit, reason));
    }
  }
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

/**
 * 与运行总览同一套行视觉语言：Faulted→红条红底，Held→黄条。
 * 全量履历里故障/保持是要人动手的状态，扫一列色条就能定位，不必逐行读 tag。
 */
function batchRowClass({ row }: { row: BatchListItemDto }): string {
  if (row.status === "Faulted") return "live-critical";
  if (row.status === "Held") return "live-warn";
  return "";
}

onMounted(async () => {
  await load();
  poll.start();});
</script>

<style scoped>
.unit-hint { color: var(--muted); font-size: 12px; margin-bottom: var(--space-2); line-height: 1.4; }
.unit-bind { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); margin-bottom: var(--space-2); }
.status-pills { display: flex; flex-wrap: wrap; gap: 4px; }
/* 与运行总览同一套行视觉语言：故障行红条+7% 红底、保持行黄条；
   全量履历里这两种是要人动手的状态，别的状态不加边（悬停底色规则同理要反压）。 */
.batch-table :deep(.live-critical td:first-child) { box-shadow: inset 3px 0 0 var(--err); }
.batch-table :deep(.live-critical td) { background-color: color-mix(in srgb, var(--err) 7%, transparent); }
.batch-table :deep(tr.live-critical:hover > td) { background-color: color-mix(in srgb, var(--err) 14%, var(--hover)); }
.batch-table :deep(.live-warn td:first-child) { box-shadow: inset 3px 0 0 var(--warn); }
</style>
