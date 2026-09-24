<template>
  <div class="handshake-bar" :class="{ faulted }">
    <div class="handshake-steps">
      <template v-for="(s, i) in steps" :key="s.key">
        <div v-if="i > 0" class="step-link" :class="{ done: s.state === 'done' || s.state === 'active' }" />
        <div class="handshake-step" :class="s.state">
          <i class="step-node">{{ s.state === "done" ? "✓" : s.key }}</i>
          <div class="step-text">
            <b><HelpTip :term="s.title">{{ $t(s.title) }}</HelpTip></b>
            <span>{{ $t(s.hint) }}</span>
          </div>
        </div>
      </template>
    </div>
    <div class="handshake-status">{{ text }}</div>
  </div>
</template>

<script setup lang="ts">
import type { HandshakeStepDef } from "../utils/handshake";
import type { HandshakeStepState } from "../utils/handshakeProgress";
import HelpTip from "./HelpTip.vue";

/**
 * 四步握手进度条（A 写参 → B 应答 → C 看门狗 → D 归档步进）。
 * 纯展示：每一步的态由 utils/handshakeProgress 算好传进来，这里不判断任何业务条件。
 */
defineProps<{
  steps: (HandshakeStepDef & { state: HandshakeStepState })[];
  text: string;
  faulted: boolean;
}>();
</script>

<style scoped>
.handshake-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--space-2) var(--space-4);
  padding: var(--space-3) var(--space-4);
  background: var(--sunken);
  border: 1px solid var(--line);
  border-radius: 10px;
}
.handshake-bar.faulted { border-color: var(--err); }
.handshake-steps {
  display: flex;
  align-items: center;
  flex: 1 1 100%;
  min-width: 0;
}
.handshake-step { display: flex; align-items: center; gap: var(--space-2); flex: 1 1 0; min-width: 0; }
.step-node {
  flex: none; width: 26px; height: 26px; border-radius: 50%;
  display: inline-flex; align-items: center; justify-content: center;
  font-size: 12px; font-style: normal;
  background: var(--panel); border: 1px solid var(--line); color: var(--muted);
}
.handshake-step.done .step-node { background: var(--tint); border-color: var(--accent); color: var(--accent-bright); }
.handshake-step.active .step-node { background: var(--accent); border-color: var(--accent-bright); color: var(--bg); }
.step-text { display: flex; flex-direction: column; min-width: 0; overflow: hidden; }
.step-text b,
.step-text span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.step-text b { font-size: 12px; font-weight: 500; color: var(--text); }
.step-text span { font-size: 11px; color: var(--muted); }
.step-link {
  flex: 0 1 24px; min-width: 8px;
  height: 2px; background: var(--line); margin: 0 8px;
}
.step-link.done { background: var(--accent); }
.handshake-status {
  flex: 1 1 100%;
  font-size: 12px;
  color: var(--text-body);
  line-height: 1.45;
  text-align: left;
  padding-top: var(--space-2);
  border-top: 1px solid var(--line);
  overflow-wrap: break-word;
}
.handshake-bar.faulted .handshake-status { color: var(--err); }
/* ≤768px：四步握手改 2×2 网格。
   横排四等分在 390px 上每步只剩 53px，标题被省略号截成「A…/B…/C…/D…」——
   这恰好是车间手机上看的第一件事。网格让标题与副标题完整换行显示；
   连接线在网格里已无方向含义，直接去掉。 */
@media (max-width: 768px) {
  .handshake-steps {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: var(--space-3) var(--space-4);
  }
  .handshake-step { flex: none; align-items: flex-start; }
  .step-link { display: none; }
  .step-text { overflow: visible; }
  .step-text b,
  .step-text span { white-space: normal; overflow: visible; text-overflow: clip; }
  .step-node { margin-top: 1px; }
}
</style>
