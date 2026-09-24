<template>
  <el-dialog
    v-model="helpOpen"
    :title="$t('快捷键')"
    width="520px"
    append-to-body
    destroy-on-close
  >
    <p class="hint">{{ tipOf("快捷键") }}</p>
    <div v-for="group in groups" :key="group.name" class="group">
      <h4>{{ $t(group.name) }}</h4>
      <HelpTip
        v-for="row in group.rows"
        :key="row.id"
        :term="row.label"
        :chord="row.chord"
        :allow-in-input="row.allowInInput"
        :extra="row.enabled ? '' : (tipOf('快捷键不可用') ?? '')"
        plain
        block
        placement="left"
      >
        <div class="row" :class="{ off: !row.enabled }">
          <span>{{ $t(row.label) }}</span>
          <kbd class="shortcut-key">{{ formatChord(row.chord) }}</kbd>
        </div>
      </HelpTip>
    </div>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted } from "vue";
import {
  eventMatchesChord,
  focusPageSearch,
  formatChord,
  isPasswordTarget,
  isTypingTarget
} from "./chords";
import HelpTip from "../components/HelpTip.vue";
import { tipOf } from "../utils/glossary";
import { collectPageShortcuts } from "./registry";
import { shortcutHelpOpen } from "./state";
import type { ShortcutSpec } from "./types";

const helpOpen = shortcutHelpOpen;

const globalSpecs: ShortcutSpec[] = [
  {
    id: "global.help",
    chord: "?",
    group: "全局",
    label: "打开或关闭本说明",
    run: () => { helpOpen.value = !helpOpen.value; }
  },
  {
    id: "global.search",
    chord: "/",
    group: "全局",
    label: "聚焦本页搜索",
    when: () => !!document.querySelector("[data-shortcut-search]"),
    run: () => { focusPageSearch(); }
  }
];

const catalog = computed(() => [...collectPageShortcuts(), ...globalSpecs]);

const groups = computed(() => {
  void helpOpen.value;
  const map = new Map<string, { id: string; chord: string; label: string; enabled: boolean; allowInInput: boolean }[]>();
  for (const spec of catalog.value) {
    const rows = map.get(spec.group) ?? [];
    rows.push({
      id: spec.id,
      chord: spec.chord,
      label: spec.label,
      enabled: spec.when ? spec.when() : true,
      allowInInput: !!spec.allowInInput
    });
    map.set(spec.group, rows);
  }
  return [...map.entries()].map(([name, rows]) => ({ name, rows }));
});

function onKeydown(e: KeyboardEvent) {
  if (e.defaultPrevented || e.isComposing || e.repeat) return;
  if (document.querySelector(".el-message-box")) return;

  if (helpOpen.value) {
    if (eventMatchesChord(e, "escape") || eventMatchesChord(e, "?")) {
      e.preventDefault();
      helpOpen.value = false;
    }
    return;
  }

  const typing = isTypingTarget(e.target);
  for (const spec of catalog.value) {
    if (!eventMatchesChord(e, spec.chord)) continue;
    if (typing && !spec.allowInInput) continue;
    if (typing && isPasswordTarget(e.target)) continue;
    if (spec.when && !spec.when()) continue;
    e.preventDefault();
    void spec.run();
    return;
  }
}

onMounted(() => window.addEventListener("keydown", onKeydown, true));
onUnmounted(() => window.removeEventListener("keydown", onKeydown, true));
</script>

<style scoped>
.hint { color: var(--muted); font-size: 12px; margin: 0 0 var(--space-4); white-space: pre-line; }
.group { margin-bottom: var(--space-4); }
.group:last-child { margin-bottom: 0; }
h4 { margin: 0 0 var(--space-2); font-size: 13px; color: var(--muted); font-weight: 600; }
.row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  width: 100%;
  padding: var(--space-2) 0;
  border-bottom: 1px solid var(--line);
  color: var(--text-body);
  font-size: 13px;
}
.row:last-child { border-bottom: 0; }
.row.off { opacity: 0.45; }
</style>
