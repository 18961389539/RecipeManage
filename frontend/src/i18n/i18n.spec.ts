import { beforeEach, describe, expect, it } from "vitest";
import en from "./locales/en";
import { currentLocale, setLocale, SUPPORTED_LOCALES, t } from "./index";

/**
 * i18n 的地基测试。界面文本正在按页迁移，这几条保证"迁一半"不会变成"坏一半"。
 */
describe("i18n 地基", () => {
  beforeEach(() => setLocale("zh-CN"));

  it("默认语言必须是中文", () => {
    // e2e 用中文界面文本定位按钮（"通过并电子签名"、"登录"），默认语言一变整套用例全红。
    expect(SUPPORTED_LOCALES[0]).toBe("zh-CN");
    setLocale("zh-CN");
    expect(currentLocale()).toBe("zh-CN");
  });

  it("中文下原样返回，且插值照常工作", () => {
    // 这条是"zh 也要有身份表"的原因：表里没这条消息的话，
    // vue-i18n 会走 missing 处理器，而它拿不到参数，界面就会显示「共 {0} 条」。
    expect(t("共 {0} 条", 7)).toBe("共 7 条");
  });

  it("英文下取译文并插值", () => {
    setLocale("en");
    expect(t("多级审核")).toBe("Approvals");
    expect(t("共 {0} 条", 7)).toBe("7 items");
  });

  it("缺译安静退回中文原文，不吐键名", () => {
    setLocale("en");
    expect(t("这条还没有英文译文")).toBe("这条还没有英文译文");
  });

  it("每条英文译文都真的翻译了", () => {
    // 键就是中文原文，所以"译文等于键"意味着这条是占位没填。
    const untouched = Object.entries(en).filter(([k, v]) => !v.trim() || v === k);
    expect(untouched).toEqual([]);
  });

  it("译文里不出现未闭合的插值占位", () => {
    // 中文写 {0} 而英文漏掉，切过去就会少一个数（"共 7 条" → "items"）。
    const broken = Object.entries(en).filter(([k, v]) => {
      const zh = (k.match(/\{\d+\}/g) ?? []).sort().join(",");
      const en_ = (v.match(/\{\d+\}/g) ?? []).sort().join(",");
      return zh !== en_;
    });
    expect(broken).toEqual([]);
  });
});
