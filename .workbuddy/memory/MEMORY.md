# BRMES 项目长期备忘（蒸馏自每日日志）

## 前端约定

- **i18n**：中文原文即 key（gettext 式）；带 `{0}` 占位的键必须进 `en.ts`（zh-CN 表从 en 键自动生成，missing 处理器收不到插值参数）；`labels.ts` 字典值也进覆盖率棘轮——新增标签必须同步 en.ts，`coverage.spec` 守门。脚本侧 `t(key, ...args)` 是变参；模板 `$t` 才收数组。
- **空值口径**：列表空值一律裸「—」，不套标签不画 0；「不知道」（无数据）画 — 且用次要色，「确认是零」才画 0。
- **表格行语义着色**：出事行左缘 3px 色条（红=Faulted、黄=Held）+ 故障行 7% 红底，用 `td:first-child` inset box-shadow 实现；**全局 `.el-table__body tr:hover > td` 底色规则特异性更高，悬停会抹掉自绘底色，必须补 `tr.xxx:hover > td` 反压**。空闲行压 `--idle` 色、`cursor: default`、el-tag 需单独 opacity 0.55。
- **echarts 按需注册**：全站唯一入口 `utils/echarts.ts`，只注册了 Bar/Line/Custom——**新图表类型不 `echarts.use` 会在运行时静默不渲染，vue-tsc 不报错**。
- **色板**：组件一律 `var(--x)`，新色先进 styles.css :root；canvas 内取色用 `palette()`（utils/theme.ts），令牌名是联合类型。
- **明暗双主题**：`utils/themeMode.ts` 的 `applyTheme` 同时设 `data-theme` 属性与 `dark` class（localStorage `brmes.theme`，默认 dark）。styles.css 里浅色令牌块是 `html[data-theme="light"]`；残留的 `html.dark ...` 是**对比度修复**（白字压主色的地方换深色墨水印），不是主题作用域，别当成半成品去清。
- **表格列宽**：**列宽 = 文本宽 + 24（`.cell` 左右 padding）+ 余量**，临界值会长 token 折行。定长串列一律配 `show-overflow-tooltip`，别让 `white-space: normal` 折行。已实测的文本宽：批次号 16 位 = 132px（12px/600）；物料号最长 `INGOT-…-S1` 21 字符 = 154px；**配方编码 15 位 = 136px（14px 字体，比表格默认大一号）**。多标签并排的列要按「最宽组合 + 4px 间隙 + 24」算（如批次状态列 = 52+64+4+24 = 144）。
- **UI 改动后必跑自动巡检**：别逐页肉眼看图（会漏）。写 playwright 脚本遍历全部路由 × 两主题，自动量化三类指标——① 单元格 `cell.height / lineHeight > 1`（折行）② `scrollWidth - innerWidth`（横向溢出）③ 文字对比度 <4.5（14px 正文）。这轮靠它一次找出肉眼漏掉的配方页 37 处折行。
- **巡检要能识别「误报」与「刻意设计」**（2026-10-14 第三轮 0 改动的教训）：`caret-wrapper`(24×14) 是 EP 排序箭头**内部结构**误报，父级 th 才是可点区；截图缩放下 12px 间距会看着像挤在一起；`.dash-main` 两表宽度差 242px 是 `minmax(200px,230px) 1fr` 的刻意栅格；帮助中心 10px 是「眉标」风格（对比度 6.6:1 达标）。**先量化再判断**，别按第一印象改。全站间距已 100% 令牌化（`grep` 无 `margin/padding` 非 `var(--space-*)` 残留）。
- **改色板层次要重扫对比度**：让某底色变亮（如斑马纹从 `--sunken` 换 `--table-zebra`）会连累压在该底色上的所有次要文字。`--idle` 语义注释写「≥3:1」，但它实际当 **14px 正文**用（总览「空闲」行），需 ≥4.5:1——注释与用法不符是漏判诱因。现深 `#7b8fb5` / 浅 `#5c6b82`，在三底色上均 ≥4.7。
- **斑马纹**：`--table-zebra`（深 `#16203a` / 浅 `#f1f6fb`），与 `--panel`/`--hover` 各 ~1.07:1。别复用 `--sunken` 当斑马行——它是凹槽语义，且深色下 1.04:1 肉眼等于纯平。

## 后端要点

- **权限模型**：`docs/auth-matrix.md` 是唯一真源。名单是**闭集**，无「管理员通配」——**管理员不碰产线**（不能启停、跳步、放行、建批次）。职责分离：放行/拒收/实验室判定**仅质量**；跳步**仅主管**；设备与握手点表**仅管理员**。前端 `auth.can(...roles)` 与后端 `CapabilitiesTests` 同口径，**改权限前先查这张表**，别把刻意分离当漏洞改掉。
- **易用性排查用「角色差分实测」**：5 角色各自独立 browser context 登录（同一 context 会因已登录被守卫弹回，必须隔离），遍历页面比对「可见按钮 + 提示文案」。路由级无权限已有提示（跳回总览+说明）；**页面内权限差异是静默的**——无权限角色的操作位会留空，需补一句 `--muted` 说明代替入口（BatchList 已做：`canCreate` controlled hint）。但**先看渲染实况再决定**：页面数据充实（如报警页）就不必加，否则是噪声。
- **单实例**：同一 DB 第二个进程拒启（退出码 75）；`dotnet run` 持有 bin DLL——重新 build 前先 `taskkill //F //IM RecipesManage.Api.exe`。
- **批次驱动 API**：POST /batches 需配方单元与设备类匹配（EQ_CLASS 校验，要遍历配方×设备组合）；`/start` 等动作 POST 需电子签名 `{ password }`，缺 body 会 400 "non-empty request body"。
- **事件存储**：四步握手事件落 `HandshakeEvents`（带 CreatedAt，Entity 基类），无导航属性到批表；批次状态历史无独立事件表，时间线类需求从 HandshakeEvents 取。
- **测试时序（已修复）**：Execution.Tests 曾有时序型 flaky 家族，根因是真实时钟窗口 + 测试集并行负载。已修：调度器与仿真器统一注入 TimeProvider（测试用 FakeTimeProvider + 后台推进器虚拟推时，窗口关键期 Pause 手动 Advance）、xunit.runner.json 关闭测试集并行。**新增调度器/引擎集成测试一律走 SchedulerHarness 的 fake 时钟模式**；hold/resume 编排类测试保持真实时钟（语义依赖实时观察顺序）。

## 验证方法（每页改后）

- 浏览器实测：playwright-core 脚本 + `NODE_PATH=<frontend>/node_modules NODE_OPTIONS= <managed node> script.mjs`；登录后可直接 `page.evaluate` 用 `localStorage.rm_token` 调 API 建数据。**NODE_OPTIONS 必须置空**，否则 SAFE_DELETE shim 会杀 Vite/pip。
- vitest/vue-tsc 输出过管道 grep 会吞退出码——显式回显或别用管道判断成败。
- 写死阈值（如开关出现条件）无法触发时，临时 HMR 改阈值实测行为再还原，比只验证"隐藏"强。

## 运行总览（Dashboard）改进史

2026-10-03 一轮清零 P0-P2：语义着色、数据新鲜度徽标、占用行分层+只看非空闲开关（阈值 8 台）、KPI 脉冲、工步列、时长列（1s 时钟）、报警摘要条、积压老化（OldestPendingReleaseAt）、按角色过滤磁贴、执行态势卡换 2h 事件时间线（HandshakeEvents 白名单 Kind）。全站 15 页同日巡检过。
