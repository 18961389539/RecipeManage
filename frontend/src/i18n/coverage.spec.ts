import { readFileSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import en from "./locales/en";

/**
 * i18n 覆盖率棘轮。
 *
 * 为什么要有：界面是**按页**迁移的，缺译文的表现是"那处仍然显示中文"——安静、不报错，
 * 一次迭代之后没人记得还剩哪些。这个测试把"用了 $t 却没登记译文"变成红灯，
 * 于是迁移进度只能往前走，不会悄悄漏。
 *
 * 扫描规则上有个真坑，别改回去：`import(` 里含子串 `t(`，
 * 光用 /t\(["']([^"']+)["']\)/ 会把 `import("../views/Dashboard.vue")` 当成界面文本
 * （实测抓到过一次，键表里冒出一堆路径）。所以要求 `t` 前面不是单词字符、`$`，也不是点号。
 */

const SRC = path.resolve(__dirname, "..");
const KEY_RE = /(?<![A-Za-z0-9_$])\$?t\(\s*(?:"([^"]+)"|'([^']+)')\s*(?:,|\))/g;
const CJK = /[一-鿿]/;

/**
 * 只许往下降的棘轮基线：界面是按页迁移的，一次性要求 100% 覆盖只会逼人把测试关掉。
 * 补了译文就得同步下调这里的数字（下面那条用例专门盯着这件事）。
 *
 * 现存 173 条全是 glossary.ts 里 `text`/`detail` 的**悬停解释正文**：界面可见的标签
 * （枚举状态、角色、审计动作、术语名）都已登记完，英文界面唯一还露中文的地方是悬停段落。
 */
const MISSING_CEILING = 0;

function sources(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const p = path.join(dir, entry);
    if (statSync(p).isDirectory()) sources(p, out);
    else if (/\.vue$|\.ts$/.test(entry) && !p.includes(`${path.sep}i18n${path.sep}`) && !entry.includes(".spec."))
      out.push(p);
  }
  return out;
}

/**
 * 字典源：labels.ts / glossary.ts / integrity.ts 在 `translate()` / `tipOf()` / 标签函数里对值调 `t()`，
 * 所以它们的中文是**运行时键**，源码里根本没有 `$t("…")` 字样。
 * 只扫调用点会一片绿、界面却全是中文——所以这几个文件的中文一律也算需要登记的键。
 */
const DICT_SOURCES = ["utils/labels.ts", "utils/glossary.ts", "utils/integrity.ts", "utils/handshake.ts"];

/**
 * 值来自后端、前端只在运行时按值查表的键。
 * 写参计划的「策略」列就是这种：文案由 ControlRecipeWritePlan.cs 生成，`$t(row.policy)`
 * 传的是变量，前端源码里找不到这个字面量，所以死键规则要放行它——不是漏译，是键的产地在另一侧。
 */
const BACKEND_VALUED_KEYS = ["PLC_Ready 后写参并回读，再置 Trigger_Write"];

/**
 * 快捷键说明面板同理：行名与分组名是 `label:` / `group:` 数据，模板里只能 `$t(row.label)`，
 * 正则扫不到字面量。声明快捷键的文件必然带 `chord:`，用这个特征把它们挑出来收 label/group。
 */
function shortcutKeys(): Map<string, string[]> {
  const map = new Map<string, string[]>();
  for (const file of sources(SRC)) {
    const text = readFileSync(file, "utf8");
    if (!text.includes("chord:")) continue;
    for (const m of text.matchAll(/(?:label|group): "([^"\n]+)"/g)) {
      const key = m[1].trim();
      if (!CJK.test(key)) continue;
      const list = map.get(key) ?? [];
      list.push(path.relative(SRC, file));
      map.set(key, list);
    }
  }
  return map;
}

/** 只去整行注释，不去行尾 `//`：文案里可能出现 `opc.tcp://` 这类串，误伤更难查。 */function stripLineComments(text: string) {
  return text.split("\n")
    .filter(l => !/^\s*(\/\/|\/\*|\*)/.test(l))
    .join("\n");
}

function dictionaryKeys(): Map<string, string[]> {
  const map = new Map<string, string[]>();
  for (const rel of DICT_SOURCES) {
    const file = path.join(SRC, rel);
    const body = stripLineComments(readFileSync(file, "utf8"));
    for (const m of body.matchAll(/"([^"\n]+)"|'([^'\n]+)'/g)) {
      const key = (m[1] ?? m[2] ?? "").trim();
      if (!CJK.test(key)) continue;
      const list = map.get(key) ?? [];
      list.push(rel);
      map.set(key, list);
    }
  }
  return map;
}

/** 只收"需要翻译"的键：含中文的界面文本。纯英文/符号（如 "PDF/A"）不进表。 */
function usedKeys(): Map<string, string[]> {
  const map = new Map<string, string[]>();
  const merge = (source: Map<string, string[]>) => {
    for (const [k, files] of source) {
      const list = map.get(k) ?? [];
      map.set(k, [...list, ...files]);
    }
  };
  merge(scanCallSites());
  merge(dictionaryKeys());
  merge(shortcutKeys());
  return map;
}

function scanCallSites(): Map<string, string[]> {
  const map = new Map<string, string[]>();
  for (const file of sources(SRC)) {
    const text = readFileSync(file, "utf8");
    for (const m of text.matchAll(KEY_RE)) {
      const key = (m[1] ?? m[2] ?? "").trim();
      if (!key || !CJK.test(key)) continue;
      const list = map.get(key) ?? [];
      list.push(path.relative(SRC, file));
      map.set(key, list);
    }
  }
  return map;
}

describe("i18n 覆盖率", () => {
  const used = usedKeys();

  it("扫到了界面文本键（防止正则退化成什么都匹配不到而测试空跑）", () => {
    expect(used.size).toBeGreaterThan(300);
  });

  it("不会把 import(...) 之类误当界面文本", () => {
    // 只排导入路径这一种真实失败模式；"设备 / PLC"、"批次执行态" 这类合法文案里本来就有斜杠。
    const suspicious = [...used.keys()].filter(k => /^(\.\.?\/|\/)/.test(k) || /\.(vue|ts)$/.test(k));
    expect(suspicious).toEqual([]);
  });

  it("调用点不传带首尾空白的键", () => {
    // 扫描器比对前会 trim，所以 `t(" · 剩余 {0}s")` 会被表里的 `· 剩余 {0}s` 当成已登记，
    // 而运行时查的是带空格那个键——找不到就直接把 {0} 显示到界面上。实测踩过一次。
    const padded: string[] = [];
    for (const file of sources(SRC)) {
      const text = readFileSync(file, "utf8");
      for (const m of text.matchAll(KEY_RE)) {
        const raw = (m[1] ?? m[2] ?? "");
        if (raw !== raw.trim()) padded.push(`${path.relative(SRC, file)}: ${JSON.stringify(raw)}`);
      }
    }
    expect(padded).toEqual([]);
  });

  it("键里没有插值残骸", () => {    // 批量抽取把 `=>` 的 > 当成标签闭合过，于是 "{{ x }} 说明文字" 被从中间包坏，
    // 键里留下反引号与花括号。界面不会报错，只会把那行渲染成乱码——所以钉死在这里。
    // {0} / {name} 这类是合法的命名插值，要放行；双花括号、反引号、落单的花括号才是残骸。
    const broken = [...used.keys()].filter(k =>
      /\{\{|\}\}|[\\`]/.test(k) || k.replace(/\{\d+\}|\{\w+\}/g, "").includes("{")
      || k.replace(/\{\d+\}|\{\w+\}/g, "").includes("}"));
    expect(broken).toEqual([]);
  });

  it("带占位的键必须登记译文（否则中文界面会露出 {0}）", () => {
    // 中文身份表只收带占位的键（无占位的走 missing 原样返回，省一份副本）。
    // 所以一旦有个带 {0} 的键漏在 en 表外，中文侧就拿不到消息、也拿不到参数，直接显示「共 {0} 条」。
    const bare = [...used.keys()].filter(k => /\{\d+\}|\{\w+\}/.test(k) && !en[k]);
    expect(bare).toEqual([]);
  });

  it("缺译数量不得超过棘轮基线", () => {
    const missing = [...used.keys()].filter(k => !en[k]);
    if (missing.length > MISSING_CEILING) {
      // 超线时把名单打出来：只报个数字等于让下一个人再写一遍扫描。
      expect([`缺译 ${missing.length}`,
        ...missing.slice(0, 60).map(k => `  ${k.slice(0, 46)}  ← ${used.get(k)![0]}`)].join("\n"))
        .toBe(`缺译 ${MISSING_CEILING}（超出部分见上方清单）`);
    }
    expect(missing.length).toBeLessThanOrEqual(MISSING_CEILING);
  });

  it("已经补了译文就把基线降下来（棘轮只许往回拧）", () => {
    const missing = [...used.keys()].filter(k => !en[k]).length;
    if (missing < MISSING_CEILING)
      throw new Error(`缺译已从 ${MISSING_CEILING} 降到 ${missing}，请把 MISSING_CEILING 改成 ${missing}。`);
  });

  it("译文表里没有已经没人用的死键", () => {
    // 侧栏与顶栏标题走的是 t(item.label) / t(route.meta.title)——键名不是 $t("…") 字面量，
    // 而是 router 里的那份名单，所以这里回扫整个 src 的字符串字面量。
    const all = sources(SRC).map(f => readFileSync(f, "utf8"));
    const dead = Object.keys(en).filter(k =>
      !BACKEND_VALUED_KEYS.includes(k) && !used.has(k) && !all.some(text => text.includes(`"${k}"`) || text.includes(`'${k}'`)));
    expect(dead).toEqual([]);
  });
});
