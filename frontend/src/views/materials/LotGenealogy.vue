<template>
  <!-- 原先根节点是 v-if="tree"：取数失败时整页空白、无任何提示，这是最糟的失败态。 -->
  <div class="page-state" v-if="error || (!tree && loading)">
    <el-alert
      v-if="error"
      type="error"
      show-icon
      :closable="false"
      :title="`谱系加载失败：${error}`"
      description="请确认该批次是否存在，或返回列表重试。"
    />
    <el-skeleton v-else :rows="6" animated />
  </div>
  <div v-if="tree">
    <div class="page-title">
      <div>
        <h2>谱系 · {{ tree.lot.lotNumber }}</h2>
        <span>{{ tree.lot.materialCode }} {{ tree.lot.materialName }} · {{ statusLabel(tree.lot.status) }}</span>
      </div>
      <div>
        <el-button @click="$router.push('/lots')">返回列表</el-button>
        <el-button v-if="tree.lot.status === 'Open'" type="primary" @click="openSplit">拆分子批</el-button>
      </div>
    </div>
    <el-row :gutter="12">
      <el-col :span="8" :xs="24">
        <el-card header="祖先批">
          <el-empty v-if="!tree.ancestors.length" description="来料根批，无祖先" />
          <ul v-else class="chain">
            <li v-for="row in tree.ancestors" :key="row.id">
              <el-link type="primary" @click="$router.push(`/lots/${row.id}`)">{{ row.lotNumber }}</el-link>
              <span class="muted"> {{ sourceLabel(row.source) }}</span>
            </li>
          </ul>
        </el-card>
      </el-col>
      <el-col :span="8" :xs="24">
        <el-card header="本批">
          <p><b>{{ tree.lot.lotNumber }}</b></p>
          <p class="muted">{{ sourceLabel(tree.lot.source) }} · {{ statusLabel(tree.lot.status) }}</p>
          <p>{{ tree.lot.quantity == null ? "数量未登记" : `${tree.lot.quantity} ${tree.lot.uom ?? ""}` }}</p>
        </el-card>
      </el-col>
      <el-col :span="8" :xs="24">
        <el-card header="子批">
          <el-empty v-if="!tree.descendants.length" description="尚未拆分" />
          <ul v-else class="chain">
            <li v-for="row in tree.descendants" :key="row.id">
              <el-link type="primary" @click="$router.push(`/lots/${row.id}`)">{{ row.lotNumber }}</el-link>
              <span class="muted"> {{ sourceLabel(row.source) }}</span>
            </li>
          </ul>
        </el-card>
      </el-col>
    </el-row>
    <el-card class="gap-before" header="关联生产批次（投料 / 产出）">
      <el-table :data="tree.uses">
        <el-table-column prop="batchNo" label="生产批" width="160" fixed />
        <el-table-column prop="role" label="角色" width="100">
          <template #default="{ row }">{{ roleLabel(row.role) }}</template>
        </el-table-column>
        <el-table-column prop="lotNumber" label="物料批" />
        <el-table-column prop="materialCode" label="物料" width="110" />
        <el-table-column label="数量" width="100">
          <template #default="{ row }">{{ row.quantity ?? "—" }}</template>
        </el-table-column>
      </el-table>
    </el-card>
    <el-dialog v-model="splitVisible" title="拆分子批" width="440px">
      <el-form label-width="100px">
        <el-form-item label="子批号"><el-input v-model="split.childLotNumber" /></el-form-item>
        <el-form-item label="数量"><el-input-number v-model="split.quantity" :min="0.001" :step="1" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="splitVisible = false">取消</el-button>
        <el-button type="primary" :loading="splitting" @click="doSplit">拆分</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import http from "../../api/http";
import type { LotGenealogyDto, MaterialLotSource, MaterialLotStatus, MaterialUseRole } from "../../api/types";
import {
  lotRoleLabel as roleLabel,
  lotSourceLabel as sourceLabel,
  lotStatusLabel as statusLabel
} from "../../utils/labels";
import { useLoad } from "../../utils/useLoad";

const route = useRoute();
const router = useRouter();
const tree = ref<LotGenealogyDto | null>(null);
const splitVisible = ref(false);
const splitting = ref(false);
const split = reactive({ childLotNumber: "", quantity: 10 });
const { loading, error, run } = useLoad();

async function load() {
  await run(http.get<LotGenealogyDto>(`/lots/${route.params.id}/genealogy`), (d) => (tree.value = d));
}

function openSplit() {
  split.childLotNumber = `${tree.value?.lot.lotNumber}-S${Date.now().toString(36).slice(-4).toUpperCase()}`;
  split.quantity = Math.max(1, Math.floor((tree.value?.lot.quantity ?? 20) / 4));
  splitVisible.value = true;
}

async function doSplit() {
  splitting.value = true;
  try {
    const { data } = await http.post(`/lots/${route.params.id}/split`, {
      childLotNumber: split.childLotNumber,
      quantity: split.quantity
    });
    splitVisible.value = false;
    ElMessage.success("已拆分");
    await router.push(`/lots/${data.id}`);
  } catch (e) {
    ElMessage.error((e as Error).message);
  } finally {
    splitting.value = false;
  }
}

onMounted(load);
watch(() => route.params.id, load);
</script>

<style scoped>
.chain { margin: 0; padding-left: var(--space-4); line-height: 1.8; }
.muted { color: var(--muted); font-size: 12px; }
</style>
