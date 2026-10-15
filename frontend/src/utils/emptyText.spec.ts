import { describe, expect, it } from "vitest";
import { ref } from "vue";
import { useEmptyText } from "./emptyText";

/**
 * 表格空态文案：加载期间必须闭嘴。
 *
 * 为什么值得单测：Element Plus 的 `:empty-text` 只在 `data.length === 0` 时渲染、不看 loading，
 * 各页踩过同一个坑——首屏闪「暂无 X」假空态（慢网实测 0.5~1.4s，与加载遮罩并存）。
 * 这个工具是唯一收口，一旦有人把 `loading.value ? ""` 改掉，这里会红。
 */

describe("useEmptyText 空态文案口径", () => {
  it("加载中不吐任何文字（抑制假空态）", () => {
    const loading = ref(true);
    const text = useEmptyText(loading, () => "暂无主配方");
    expect(text.value).toBe("");
  });

  it("加载结束后恢复真实空态文案", () => {
    const loading = ref(false);
    const text = useEmptyText(loading, () => "暂无主配方");
    expect(text.value).toBe("暂无主配方");
  });

  it("loading 翻转时是响应式的（computed 随依赖更新）", () => {
    const loading = ref(true);
    const text = useEmptyText(loading, () => "暂无物料批次");
    expect(text.value).toBe("");
    loading.value = false;
    expect(text.value).toBe("暂无物料批次");
    loading.value = true;
    expect(text.value).toBe("");
  });

  it("resolve 惰性求值：依赖的响应式条件变化时文案跟着变", () => {
    const loading = ref(false);
    const query = ref("");
    const text = useEmptyText(loading, () => (query.value ? "没有匹配的设备" : "暂无设备"));
    expect(text.value).toBe("暂无设备");
    query.value = "炉";
    expect(text.value).toBe("没有匹配的设备");
  });

  it("加载中即使条件变化也不吐字（优先级：loading 高于一切）", () => {
    const loading = ref(true);
    const query = ref("炉");
    const text = useEmptyText(loading, () => (query.value ? "没有匹配的设备" : "暂无设备"));
    expect(text.value).toBe("");
    query.value = "";
    expect(text.value).toBe("");
  });
});
