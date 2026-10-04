<template>
  <!-- 原先根节点是 v-if="tree"：取数失败时整页空白、无任何提示，这是最糟的失败态。 -->
  <div class="page-state" v-if="error || (!tree && loading)">
    <el-alert
      v-if="error"
      type="error"
      show-icon
      :closable="false"
      :title="$t('谱系加载失败：{0}', [error])"
      :description="$t('请确认该批次是否存在，或返回列表重试。')"
    />
    <el-skeleton v-else :rows="6" animated />
  </div>
  <div v-if="tree">
    <div class="page-title">
      <div>
        <h2>{{ $t("谱系 · {0}", [tree.lot.lotNumber]) }}<PageGuideButton guide-key="lotGenealogy" /></h2>
        <span>{{ tree.lot.materialCode }} {{ tree.lot.materialName }} · {{ statusLabel(tree.lot.status) }}</span>
      </div>
      <div>
        <el-button @click="$router.push('/lots')">{{ $t("返回列表") }}</el-button>
        <el-button v-if="tree.lot.status === 'Open'" type="primary" @click="openSplit">{{ $t("拆分子批") }}</el-button>
      </div>
    </div>
    <div class="lineage-map">
      <section class="lineage-column">
        <div class="lineage-heading">
          <h3>{{ $t("祖先批") }}</h3>
          <span class="lineage-count">{{ tree.ancestors.length }}</span>
        </div>
        <p v-if="!tree.ancestors.length" class="none-note">{{ $t("来料根批，无祖先。") }}</p>
        <ul v-else class="chain">
          <li v-for="row in tree.ancestors" :key="row.id">
            <el-link type="primary" @click="$router.push(`/lots/${row.id}`)">{{ row.lotNumber }}</el-link>
            <span class="muted">{{ sourceLabel(row.source) }}</span>
          </li>
        </ul>
      </section>
      <section class="lineage-column lineage-current">
        <div class="lineage-heading">
          <h3>{{ $t("本批") }}</h3>
          <span class="lineage-state">{{ statusLabel(tree.lot.status) }}</span>
        </div>
        <strong class="lineage-lot-number">{{ tree.lot.lotNumber }}</strong>
        <div class="lineage-details">
          <span>{{ sourceLabel(tree.lot.source) }}</span>
          <span>{{ tree.lot.quantity == null ? $t("数量未登记") : `${tree.lot.quantity} ${tree.lot.uom ?? ""}` }}</span>
        </div>
      </section>
      <section class="lineage-column">
        <div class="lineage-heading">
          <h3>{{ $t("子批") }}</h3>
          <span class="lineage-count">{{ tree.descendants.length }}</span>
        </div>
        <p v-if="!tree.descendants.length" class="none-note">{{ $t("尚未拆分子批。") }}</p>
        <ul v-else class="chain">
          <li v-for="row in tree.descendants" :key="row.id">
            <el-link type="primary" @click="$router.push(`/lots/${row.id}`)">{{ row.lotNumber }}</el-link>
            <span class="muted">{{ sourceLabel(row.source) }}</span>
          </li>
        </ul>
      </section>
    </div>
    <el-card class="gap-before" :header="$t('关联生产批次（投料 / 产出）')">
      <p class="mobile-table-hint">{{ $t("窄屏可左右滑动物料谱系表查看其他字段。") }}</p>
      <el-table class="genealogy-uses-table" :data="tree.uses" :empty-text="$t('本批未关联生产批次')">
        <el-table-column prop="batchNo" :label="$t('生产批')" width="160" fixed />
        <el-table-column prop="role" :label="$t('角色')" width="100">
          <template #default="{ row }">{{ roleLabel(row.role) }}</template>
        </el-table-column>
        <el-table-column prop="lotNumber" :label="$t('物料批')" />
        <el-table-column prop="materialCode" :label="$t('物料')" width="110" />
        <el-table-column :label="$t('数量')" width="100">
          <template #default="{ row }">{{ row.quantity ?? "—" }}</template>
        </el-table-column>
      </el-table>
    </el-card>
    <el-dialog v-model="splitVisible" :title="$t('拆分子批')" width="440px">
      <el-form label-width="100px">
        <el-form-item :label="$t('子批号')"><el-input v-model="split.childLotNumber" /></el-form-item>
        <el-form-item :label="$t('数量')"><el-input-number v-model="split.quantity" :min="0.001" :step="1" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="splitVisible = false">{{ $t("取消") }}</el-button>
        <el-button type="primary" :loading="splitting" @click="doSplit">{{ $t("拆分") }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import { t } from "../../i18n";
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
    ElMessage.success(t("已拆分"));
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
.lineage-map {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1.15fr) minmax(0, 1fr);
  align-items: stretch;
  gap: var(--space-3);
}
.lineage-column {
  position: relative;
  min-width: 0;
  padding: var(--space-3);
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--sunken);
}
.lineage-column:not(:last-child)::after {
  content: "›";
  position: absolute;
  z-index: 1;
  top: 50%;
  right: calc(-1 * var(--space-3));
  width: var(--space-3);
  color: var(--muted);
  font-size: 20px;
  line-height: 1;
  text-align: center;
  transform: translateY(-50%);
}
.lineage-current {
  border-color: var(--accent);
  background: var(--tint);
  box-shadow: inset 0 0 0 1px var(--accent);
}
.lineage-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-2);
  margin-bottom: var(--space-2);
}
.lineage-heading h3 {
  margin: 0;
  color: var(--text-body);
  font-size: 12px;
  font-weight: 600;
}
.lineage-count,
.lineage-state {
  flex: none;
  padding: 2px 8px;
  border: 1px solid var(--line);
  border-radius: 999px;
  color: var(--muted);
  font-size: 11px;
  line-height: 1.4;
}
.lineage-current .lineage-state {
  border-color: var(--accent);
  color: var(--accent-bright);
}
.lineage-lot-number {
  display: block;
  overflow-wrap: anywhere;
  color: var(--text);
  font-size: 15px;
  line-height: 1.4;
}
.lineage-details {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  margin-top: var(--space-2);
  color: var(--muted);
  font-size: 12px;
}
.lineage-column .none-note { margin: 0; padding: var(--space-2) 0 0; }
.lineage-column .chain { padding-left: 0; list-style: none; }
.lineage-column .chain li { display: flex; flex-wrap: wrap; align-items: baseline; gap: 2px 8px; }
.mobile-table-hint { display: none; }
.muted { color: var(--muted); font-size: 12px; }
@media (max-width: 768px) {
  .lineage-map { grid-template-columns: minmax(0, 1fr); gap: var(--space-3); }
  .lineage-column:not(:last-child)::after {
    content: "↓";
    top: auto;
    right: auto;
    bottom: calc(-1 * var(--space-3));
    left: 50%;
    transform: translateX(-50%);
  }
  .mobile-table-hint { display: block; }
}
</style>
