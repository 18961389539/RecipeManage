/** 快捷键和弦解析与匹配。展示文案集中在这里，避免各页各写 Ctrl / ⌘。 */

import { t } from "../i18n";

export interface ParsedChord {
  key: string;
  ctrl: boolean;
  shift: boolean;
  alt: boolean;
}

const MAC = typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform);

export function parseChord(chord: string): ParsedChord {
  const parts = chord.trim().toLowerCase().split("+").filter(Boolean);
  const key = parts.pop() ?? "";
  return {
    key,
    ctrl: parts.includes("ctrl") || parts.includes("cmd") || parts.includes("meta"),
    shift: parts.includes("shift"),
    alt: parts.includes("alt")
  };
}

export function eventMatchesChord(e: KeyboardEvent, chord: string): boolean {
  const want = parseChord(chord);
  const key = eventKey(e);
  const ctrl = e.ctrlKey || e.metaKey;
  if (want.key === "?")
    return (key === "?" || (e.shiftKey && key === "/")) && !ctrl && !e.altKey;
  if (want.key === "/")
    return key === "/" && !e.shiftKey && !ctrl && !e.altKey;
  if (key !== want.key) return false;
  if (want.ctrl !== ctrl) return false;
  if (want.alt !== e.altKey) return false;
  if (want.shift && !e.shiftKey) return false;
  return true;
}

function eventKey(e: KeyboardEvent): string {
  if (e.key === "Escape") return "escape";
  if (e.key === "Enter") return "enter";
  if (e.key === " ") return "space";
  if (/^f\d+$/i.test(e.key)) return e.key.toLowerCase();
  if (e.key.length === 1) return e.key.toLowerCase();
  return e.key.toLowerCase();
}

export function formatChord(chord: string): string {
  const want = parseChord(chord);
  const bits: string[] = [];
  if (want.ctrl) bits.push(MAC ? "⌘" : "Ctrl");
  if (want.alt) bits.push(MAC ? "⌥" : "Alt");
  if (want.shift) bits.push(MAC ? "⇧" : "Shift");
  bits.push(keyLabel(want.key));
  return bits.join(MAC ? "" : "+");
}

/** Tooltip / 帮助里的「快捷键 …」行，避免各页各写一句。 */
export function chordHint(chord: string, allowInInput = false): string {
  const keys = formatChord(chord);
  return allowInInput
    ? t("快捷键 {0}，在输入框里也可按。", keys)
    : t("快捷键 {0}。", keys);
}

function keyLabel(key: string): string {
  if (key === "escape") return "Esc";
  if (key === "enter") return "Enter";
  if (key === "space") return t("空格");
  if (/^f\d+$/.test(key)) return key.toUpperCase();
  if (key.length === 1) return key.toUpperCase();
  return key;
}

export function isTypingTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  const el = target.closest("input, textarea, select, [contenteditable='true'], [role='textbox']");
  return !!el;
}

export function isPasswordTarget(target: EventTarget | null): boolean {
  return target instanceof HTMLInputElement && target.type === "password";
}

export function focusPageSearch(): boolean {
  const root = document.querySelector("[data-shortcut-search]");
  if (!root) return false;
  const input = (root instanceof HTMLInputElement ? root : root.querySelector("input")) as HTMLInputElement | null;
  if (!input) return false;
  input.focus();
  input.select();
  return true;
}
