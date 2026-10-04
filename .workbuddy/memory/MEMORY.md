# BRMES 项目长期备忘（蒸馏自每日日志）

## 前端约定

- **i18n**：中文原文即 key（gettext 式）；带 `{0}` 占位的键必须进 `en.ts`（zh-CN 表从 en 键自动生成，missing 处理器收不到插值参数）；`labels.ts` 字典值也进覆盖率棘轮——新增标签必须同步 en.ts，`coverage.spec` 守门。脚本侧 `t(key, ...args)` 是变参；模板 `$t` 才收数组。
- **空值口径**：列表空值一律裸「—」，不套标签不画 0；「不知道」（无数据）画 — 且用次要色，「确认是零」才画 0。
- **表格行语义着色**：出事行左缘 3px 色条（红=Faulted、黄=Held）+ 故障行 7% 红底，用 `td:first-child` inset box-shadow 实现；**全局 `.el-table__body tr:hover > td` 底色规则特异性更高，悬停会抹掉自绘底色，必须补 `tr.xxx:hover > td` 反压**。空闲行压 `--idle` 色、`cursor: default`、el-tag 需单独 opacity 0.55。
- **echarts 按需注册**：全站唯一入口 `utils/echarts.ts`，只注册了 Bar/Line/Custom——**新图表类型不 `echarts.use` 会在运行时静默不渲染，vue-tsc 不报错**。
- **色板**：组件一律 `var(--x)`，新色先进 styles.css :root；canvas 内取色用 `palette()`（utils/theme.ts），令牌名是联合类型。

## 后端要点

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
