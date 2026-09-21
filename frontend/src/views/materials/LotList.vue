<template>
  <div>
    <div class="page-title">
      <h2>物料批次谱系</h2>
      <el-button type="primary" @click="openCreate">登记来料批</el-button>
    </div>
    <el-alert class="gap-after"
      :closable="false"
      type="info"
      show-icon
      title="来料批可拆分成子批投料；生产批次号写入控制配方快照（不纳入完整性哈希）。实验室样品与 PLC 测点分开归档。"
     
    />
    <el-alert class="gap-after"
      v-if="error"
      :closable="false"
      type="error"
      :title="`物料批次加载失败：${error}`"
      show-icon
     
    />
    <el-table
      :data="items"
      v-loading="loading"
      class="clickable-rows"
      empty-text="暂无物料批次"
      @row-click="(row: MaterialLotDto) => $router.push(`/lots/${row.id}`)"
    >
      <el-table-column prop="lotNumber" label="批次号" width="180" fixed />
      <el-table-column prop="materialCode" label="物料" width="110" />
      <el-table-column prop="materialName" label="名称" />
      <el-table-column prop="source" label="来源" width="100">
        <template #default="{ row }">{{ sourceLabel(row.source) }}</template>
      </el-table-column>
      <el-table-column prop="status" label="状态" width="100">
        <template #default="{ row }">
          <el-tag size="small" :type="lotStatusTagType(row.status)" effect="dark">{{ statusLabel(row.status) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="数量" width="120">
        <template #default="{ row }">{{ row.quantity == null ? "—" : `${row.quantity}${row.uom ?? ""}` }}</template>
      </el-table-column>
    </el-table>
    <el-dialog v-model="visible" title="登记来料批" width="480px">
      <el-form label-width="100px">
        <el-form-item label="批次号"><el-input v-model="form.lotNumber" /></el-form-item>
        <el-form-item label="物料编码"><el-input v-model="form.materialCode" /></el-form-item>
        <el-form-item label="物料名称"><el-input v-model="form.materialName" /></el-form-item>
        <el-form-item label="数量"><el-input-number v-model="form.quantity" :min="0.001" :step="1" /></el-form-item>
        <el-form-item label="单位"><el-input v-model="form.uom" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="visible = false">取消</el-button>
        <el-button type="primary" :loading="creating" @click="create">登记</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from "vue";
import { useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import http from "../../api/http";
import type { MaterialLotDto, MaterialLotSource, MaterialLotStatus } from "../../api/types";
import { lotSourceLabel as sourceLabel, lotStatusLabel as statusLabel, lotStatusTagType } from "../../utils/labels";
import { useLoad } from "../../utils/useLoad";

const router = useRouter();
const items = ref<MaterialLotDto[]>([]);
const visible = ref(false);
const creating = ref(false);
const { loading, error, run } = useLoad();
const form = reactive({ lotNumber: "", materialCode: "AL6061", materialName: "铝合金锭", quantity: 100, uom: "kg" });

async function load() {
  await run(http.get<MaterialLotDto[]>("/lots"), (d) => (items.value = d));
}

function openCreate() {
  form.lotNumber = `INGOT-${Date.now().toString(36).toUpperCase()}`;
  visible.value = true;
}

async function create() {
  creating.value = true;
  try {
    await http.post("/lots", { ...form });
    visible.value = false;
    ElMessage.success("已登记来料批");
    await load();
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    creating.value = false;
  }
}

onMounted(load);
</script>
