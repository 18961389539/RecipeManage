import { ref } from "vue";

export type ThemeMode = "dark" | "light";

const STORAGE_KEY = "brmes.theme";

function readSavedTheme(): ThemeMode {
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    return saved === "light" || saved === "dark" ? saved : "dark";
  } catch {
    return "dark";
  }
}

export const themeMode = ref<ThemeMode>(readSavedTheme());

export function applyTheme(mode: ThemeMode) {
  themeMode.value = mode;
  if (typeof document !== "undefined") {
    const root = document.documentElement;
    root.dataset.theme = mode;
    root.classList.toggle("dark", mode === "dark");
    root.style.colorScheme = mode;
  }
  try {
    localStorage.setItem(STORAGE_KEY, mode);
  } catch {
    // The current session still switches themes if storage is unavailable.
  }
}

export function toggleTheme() {
  applyTheme(themeMode.value === "dark" ? "light" : "dark");
}
