import { createI18n } from "vue-i18n";
import zhCn from "element-plus/es/locale/lang/zh-cn";
import enUs from "element-plus/es/locale/lang/en";
import en from "./locales/en";

/**
 * 中英双语，中文原文即 key（gettext 式的 source-text-as-key）。
 *
 * 为什么不另造 `recipe.designer.saveBtn` 这类键名：本仓中文原生，视图里 977 处界面文本
 * 直接就是中文，换成代号等于把同一句话维护两份，而且漏换的那处不会报错、只会一直显示中文。
 * 用原文当键，漏翻译表现为"这处还是中文"——看得见，也不会把界面打成 `recipes.save.btn`。
 *
 * 一个反直觉的必要件：**带占位的中文键必须真的进消息表**。`locales["zh-CN"]` 里查不到的键
 * 会走 missing 处理器，而它**收不到插值参数**（实测），
 * 于是 `t("共 {0} 条", 7)` 会原样显示「共 {0} 条」。所以中文表不是完全不建，而是只建
 * 带 `{0}`/`{name}` 的那几个——无占位的键返回自身即可，省掉整份文案的第二副本。
 * 缺译时的表现是"这处还是中文"——看得见，也不会把界面打成 `recipes.save.btn`。
 *
 * 默认 zh-CN 不能改：e2e 用中文界面文本定位按钮（`通过并电子签名`、`登录`），
 * 默认语言一变整套用例全红。
 */

export type AppLocale = "zh-CN" | "en";
const STORAGE_KEY = "brmes.locale";

export const SUPPORTED_LOCALES: AppLocale[] = ["zh-CN", "en"];

function readSaved(): AppLocale {
  const saved = typeof localStorage === "undefined" ? null : localStorage.getItem(STORAGE_KEY);
  return saved === "en" || saved === "zh-CN" ? saved : "zh-CN";
}

/**
 * 中文身份表只需要**带占位**的键。
 *
 * 无参的 `t("刷新")` 走 missing 处理器拿回键本身，结果与"查到自己"一样，所以 358 个键里
 * 绝大多数不必再存一份中文；但带参数的必须由消息引擎亲自插值——missing 处理器收不到参数
 * （签名是 `(locale, key, instance, type)`，实测），表里没有就会把「共 {0} 条」原样显示出来。
 * 这样中文表从 ~358 条降到个位数，省掉整份文案的第二副本。
 * 代价是"带占位的键得进表"，coverage.spec 里有一条断言专门守这个。
 */
const PLACEHOLDER = /\{\d+\}|\{\w+\}/;

export const i18n = createI18n({
  legacy: false,
  globalInjection: true,
  locale: readSaved(),
  fallbackLocale: "zh-CN",
  messages: {
    "zh-CN": Object.fromEntries(Object.keys(en).filter(k => PLACEHOLDER.test(k)).map(k => [k, k])),
    en
  },
  // 缺译时安静退回中文原文；不弹 "[intlify] Not found ..." 也不要显示键名，
  // 车间终端上一条 console 警告没人会看，但满屏的 key 名会让操作员以为系统坏了。
  missing: (_locale, key) => key
});

/** Element Plus 自带文本（分页、日期面板、确定/取消）跟着切；业务文案走我们自己的表。 */
export const elementLocale = () => (i18n.global.locale.value === "en" ? enUs : zhCn);

export function currentLocale(): AppLocale {
  return i18n.global.locale.value as AppLocale;
}

export function setLocale(locale: AppLocale) {
  i18n.global.locale.value = locale;
  if (typeof localStorage !== "undefined") localStorage.setItem(STORAGE_KEY, locale);
  if (typeof document !== "undefined") document.documentElement.lang = locale;
}

/**
 * 组件外的中文（服务层 toast、标签字典、快捷键说明）也走这里。
 * 模板里用全局注入的 `$t`，脚本里用这个 `t`——同一个实例，两种入口。
 */
/**
 * 脚本侧翻译。参数收 null/undefined 是因为视图里大量插值来自可选字段
 * （`e.occupyingBatchNo` 这类，调用处已被条件挡住），让每个调用点补 `?? ""` 只会淹没真正的逻辑。
 */
export function t(key: string, ...args: (string | number | null | undefined)[]): string {
  if (!args.length) return i18n.global.t(key);
  return i18n.global.t(key, args.map(a => (a === null || a === undefined ? "" : a)));
}
