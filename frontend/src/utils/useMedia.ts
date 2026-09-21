import { onUnmounted, ref } from "vue";

/**
 * 窄屏判定。断点与 styles.css 里的 @media 保持一致：768px 以下按手机处理。
 * 用 matchMedia 而非 resize 监听 innerWidth，避免每次滚动都重算。
 */
export function useMediaQuery(query: string) {
  const mql = window.matchMedia(query);
  const matches = ref(mql.matches);
  const onChange = (e: MediaQueryListEvent) => {
    matches.value = e.matches;
  };
  mql.addEventListener("change", onChange);
  onUnmounted(() => mql.removeEventListener("change", onChange));
  return matches;
}

export function useIsMobile() {
  return useMediaQuery("(max-width: 768px)");
}
