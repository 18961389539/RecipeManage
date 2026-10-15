# BRMES 项目长期备忘（蒸馏自每日日志）

## 前端约定

- **i18n**：中文原文即 key（gettext 式）；带 `{0}` 占位的键必须进 `en.ts`（zh-CN 表从 en 键自动生成，missing 处理器收不到插值参数）；`labels.ts` 字典值也进覆盖率棘轮——新增标签必须同步 en.ts，`coverage.spec` 守门。脚本侧 `t(key, ...args)` 是变参；模板 `$t` 才收数组。
- **i18n 棘轮有结构性盲区：裸字面量扫不到**。`KEY_RE` 只匹配 `t(...)` **调用内部**的字符串，所以 `{{ row.drifted ? "是" : "否" }}`（字面量不在调用里）它**结构上看不见**——棘轮全绿、英文界面却露中文。`coverage.spec.ts` 第 9 条用例专门补这个盲区（**别删**）：先把每个 `$t/t/tipOf(...)` 调用整段抠掉（按括号配对，含带数组参、嵌套三元、字符串内括号），再看剩余是否含中文；仅豁免 `AppShell.vue` 的 `locale === "en" ? "中文" : "EN"`。判据**不能**写成「表达式是否以 t 调用开头」——`row.oos ? $t("超差") : $t("合格")` 是合法的，那样会大面积误报（实测误报 12 项）。另：`PageGuideButton.vue` 每个 guide 都有 `"zh-CN"` + `en` 两套，是**正确双语**，别当硬编码中文改。
- **第二个 i18n 盲区：`$t(条件 ? "A" : "B")` 运行时选键进不了棘轮**。`KEY_RE` 只认「紧跟左括号的字面量」，两个分支**都统计不到**（实测命中 0）——漏译不报警、界面露中文。**新增的长句式键严禁用这种写法**（既有 `$t(row.x ? "是" : "否")` 安全是因为"是/否"在别处也作为独立 `$t` 出现过、早已收录）；改用模板级 `<template v-if>` + 两个独立 `$t("字面量")`。附带：**注释里别写出 `$t("中文")` 形态**，会被棘轮当真键扫走并报「缺译」。
- **空态引导（新装机）**：真空库时只给「暂无 XX」是没用的——用户不知道从哪开始、上下游是什么。范本是批次页/设备页 + 物料批/主配方的 `<div v-if="!loading && !error && !items.length && !query.trim()" class="muted gap-before">`，**三个前置条件缺一不可**（加载中不显示、出错不显示——那时"没有数据"是假信息、筛选中不显示——用户要的是"没匹配"）。**但判据不是「空态有没有引导」，而是「这个空态在新装机时会不会真出现」**：用户页（账号表必非空）、审批链页（已有 `chain-empty` 流程图引导）、审计页（措辞已含"符合条件"）、报警页（副标题已写来源）都**不该**加。验证要覆盖四态：真空库出现 / 有数据消失 / 搜无结果消失 / 503 消失且有错误条。
- **空值口径**：列表空值一律裸「—」，不套标签不画 0；「不知道」（无数据）画 — 且用次要色，「确认是零」才画 0。
- **表格行语义着色**：出事行左缘 3px 色条（红=Faulted、黄=Held）+ 故障行 7% 红底，用 `td:first-child` inset box-shadow 实现；**全局 `.el-table__body tr:hover > td` 底色规则特异性更高，悬停会抹掉自绘底色，必须补 `tr.xxx:hover > td` 反压**。空闲行压 `--idle` 色、`cursor: default`、el-tag 需单独 opacity 0.55。
- **echarts 按需注册**：全站唯一入口 `utils/echarts.ts`，只注册了 Bar/Line/Custom——**新图表类型不 `echarts.use` 会在运行时静默不渲染，vue-tsc 不报错**。
- **色板**：组件一律 `var(--x)`，新色先进 styles.css :root；canvas 内取色用 `palette()`（utils/theme.ts），令牌名是联合类型。
- **明暗双主题**：`utils/themeMode.ts` 的 `applyTheme` 同时设 `data-theme` 属性与 `dark` class（localStorage `brmes.theme`，默认 dark）。styles.css 里浅色令牌块是 `html[data-theme="light"]`；残留的 `html.dark ...` 是**对比度修复**（白字压主色的地方换深色墨水印），不是主题作用域，别当成半成品去清。
- **表格列宽**：**列宽 = 文本宽 + 24（`.cell` 左右 padding）+ 余量**，临界值会长 token 折行。定长串列一律配 `show-overflow-tooltip`，别让 `white-space: normal` 折行。已实测的文本宽：批次号 16 位 = 132px（12px/600）；物料号最长 `INGOT-…-S1` 21 字符 = 154px；**配方编码 15 位 = 136px（14px 字体，比表格默认大一号）**。多标签并排的列要按「最宽组合 + 4px 间隙 + 24」算（如批次状态列 = 52+64+4+24 = 144）。
- **UI 改动后必跑自动巡检**：别逐页肉眼看图（会漏）。写 playwright 脚本遍历全部路由 × 两主题，自动量化三类指标——① 单元格 `cell.height / lineHeight > 1`（折行）② `scrollWidth - innerWidth`（横向溢出）③ 文字对比度 <4.5（14px 正文）。这轮靠它一次找出肉眼漏掉的配方页 37 处折行。
- **巡检要能识别「误报」与「刻意设计」**（2026-10-14 第三轮 0 改动的教训）：`caret-wrapper`(24×14) 是 EP 排序箭头**内部结构**误报，父级 th 才是可点区；截图缩放下 12px 间距会看着像挤在一起；`.dash-main` 两表宽度差 242px 是 `minmax(200px,230px) 1fr` 的刻意栅格；帮助中心 10px 是「眉标」风格（对比度 6.6:1 达标）。**先量化再判断**，别按第一印象改。全站间距已 100% 令牌化（`grep` 无 `margin/padding` 非 `var(--space-*)` 残留）。
- **改色板层次要重扫对比度**：让某底色变亮（如斑马纹从 `--sunken` 换 `--table-zebra`）会连累压在该底色上的所有次要文字。`--idle` 语义注释写「≥3:1」，但它实际当 **14px 正文**用（总览「空闲」行），需 ≥4.5:1——注释与用法不符是漏判诱因。现深 `#7b8fb5` / 浅 `#5c6b82`，在三底色上均 ≥4.7。
- **playwright 故障注入别用通配 route**（2026-10-15 第 8 轮踩坑）：`page.route("**/api/recipes*")` 会**同时命中 Vite 的源码模块** `/src/api/recipesTransfer.ts`、`/src/api/equipment.ts?t=...`——源码被 503、真正的 XHR 毫发无伤，页面照常不报错，据此会误判「某页加载失败无提示」并去修不存在的 bug。**正确做法**：谓词按 pathname 精确匹配，且记住 XHR 走 vite proxy 是**相对路径**（`http://localhost:5173/api/recipes`）而非 API 直连（`localhost:5010`）：
  `ctx.route((url) => { const p = new URL(url.href).pathname; return p === ep || p.startsWith(ep + "/"); }, ...)`；`ctx.route` 要注册在 login **之前**。
- **错误态必须有恢复出口**：全站 8 页失败时都会弹 `el-alert--error`（文案走 `useLoad` 的 `error`），但出口不均——有轮询的页（总览/批次/设备 4s）能自愈，审计/用户有「刷新」按钮，**其余页曾只能刷新浏览器**。加出口的写法：把 alert 的 `:title` 改成 `#title` 插槽，内放 `span.alert-title-row`（inline-flex + `gap: var(--space-3)`）= 错误文案 + `<el-button link type="danger" :loading="loading" @click="load">重新加载</el-button>`；`load` 各页已有，不必新写。
- **列表行键盘可达性**：行可点（`class="clickable-rows"` + `@row-click`）的表格**必须**接 `useKeyboardRows(tableRef, () => rows.value)` 并给 `<el-table ref="tableRef">`——否则键盘用户选不中任何行。别只看「可聚焦行数=0」判缺陷：行不可点、操作在列内按钮的页（设备/用户）按钮天然可聚焦，0 可聚焦行是正常的。
- **表单对话框防误关**：新建对话框一律挂 `:before-close="guardClose"`（`utils/useDialogGuard.ts`），并在「铺完表单」处调 `markClean()`。用 `before-close` 而非拦 Esc，因为它是唯一同时覆盖 Esc/遮罩/× 三条关闭路径的钩子。**序列化比差异前必须按键字典序重排**（表单是 `Object.assign` 铺的，键序不稳，直接 stringify 会误判有改动）。措辞沿用审批链页口径「表单里有未保存的修改…」。新增 i18n 键**先 grep en.ts**（「放弃未保存的修改」已存在，重复添加会 TS1117）。
- **危险操作保护**：安全关键动作全走 `utils/esign.ts` 电子签名（密码 + 必填原因），确认框文案要讲**后果**而非「确定吗」（范本：「向 {0} 注入「{1}」？若该设备上有运行中批次，会进入故障或保持。」）。判定「缺确认」前**先读后端**——有些风险服务端已挡（如 VACUUM 走 `DatabaseMaintenance.Run(idleForVacuum)`，有批次在跑就只 optimize 不重写）。**别一刀切给所有按钮补确认**，只补代价真的高的，否则确认疲劳削弱真警报。
- **路由陷阱**：审批链配置在**顶层 `/approval-chains`**，不是 `/recipes/approval-chains`——后者被 `recipes/:id` 抢走（静态段优先级高于动态段），落到配方详情页报「配方详情加载失败：接口不存在」。
- **斑马纹**：`--table-zebra`（深 `#16203a` / 浅 `#f1f6fb`），与 `--panel`/`--hover` 各 ~1.07:1。别复用 `--sunken` 当斑马行——它是凹槽语义，且深色下 1.04:1 肉眼等于纯平。

## Git 与提交

- **两个 remote，别推错**：`origin` = 本地裸库 `D:/SourceCode/RecipesManage-remote.git`；`github` = `https://github.com/18961389539/RecipeManage.git`。**`master` 跟踪的是 `github/master`**（所以 `status` 的 `ahead N` 指 GitHub）。推 `origin` 只进本地裸库、GitHub 不动——要上 GitHub 必须显式 `git push github master`。
- **推送配方**（秒级完成，比默认快几十倍）：`env -u HTTPS_PROXY -u HTTP_PROXY -u https_proxy -u http_proxy GIT_TERMINAL_PROMPT=0 git -c credential.helper= -c credential.helper=manager -c http.proxy= -c https.proxy= -c http.sslBackend=openssl push github master`。关键是清空 credential.helper 链（默认 `selector` 包装器会拉起编辑器干等）+ `sslBackend=openssl`。
- **把混合改动拆成多个 commit**：`styles.css` / `en.ts` 这类文件里，自己的改动常与在途他人改动**同 hunk**。可拆的（hunk 独立）：写单 hunk patch → `git apply --cached patch`。**不可拆的判据**：若自己的行落在一个 **HEAD 中不存在的新增块**内（用 `git show HEAD:<file> | grep` 确认），硬拆会产出「定义了一半令牌」的半截状态——此时整体归入对应 commit，并让用户在两者间选。
- **验证「单个 commit 可独立构建」**：用 `git stash push -k -u -- . ':(exclude)<要保持的文件>'` 临时收起其他批次改动（`-k` 保暂存区），只留目标 commit 的改动在树里跑 vtsc/vitest，再 `git stash pop` 还原跑全集。
- `dotnet build` 报 MSB3027/MSB3021「文件被锁」不是代码错——遗留 `dotnet run` 占着 bin DLL，`taskkill //F //IM RecipesManage.Api.exe` 后重建即过。

## 后端要点

- **权限模型**：`docs/auth-matrix.md` 是唯一真源。名单是**闭集**，无「管理员通配」——**管理员不碰产线**（不能启停、跳步、放行、建批次）。职责分离：放行/拒收/实验室判定**仅质量**；跳步**仅主管**；设备与握手点表**仅管理员**。前端 `auth.can(...roles)` 与后端 `CapabilitiesTests` 同口径，**改权限前先查这张表**，别把刻意分离当漏洞改掉。
- **易用性排查用「角色差分实测」**：5 角色各自独立 browser context 登录（同一 context 会因已登录被守卫弹回，必须隔离），遍历页面比对「可见按钮 + 提示文案」。路由级无权限已有提示（跳回总览+说明）；**页面内权限差异是静默的**——无权限角色的操作位会留空，需补一句 `--muted` 说明代替入口（`readonly-hint`，已做在 BatchList 与 RecipeList）。但**先看渲染实况再决定**：页面数据充实（如报警页）就不必加，否则是噪声。该提示样式已从 scoped 提升到 `styles.css` 全局，两页共用。
- **「去处理」类 CTA 必须带上下文**：总览的报警摘要条跳 `/alarms` 时带 `?batch=<批次号>`，AlarmList 用 `useRoute` 预填进搜索框（复用现成控件，不新增筛选）。**带 query 的跳转是本仓库既有模式**（Dashboard 的 `openBatches(query)` 传 `lab`/`status`；BatchList 读 `route.query` 并 `watch`），新加跳转应循此例，别推裸路径。
- **路由真相**：侧栏只有这 9 条 `/dashboard /help /recipes /batches /lots /alarms /equipment /audit /users`。`/procedure`、`/execution`、`/materials` **不是路由**（会落 NotFound）。详情页是 `/recipes/:id`、`/batches/:id`、`/lots/:id`。页面 h2 尾随的「?」是 `PageGuideButton` 帮助入口，不是脏字符。
- **API 开发端口是 5010**（`frontend/vite.config` proxy target `http://localhost:5010`），不是 5129。
- **列表页状态记忆（`utils/useListState.ts`）**：批次/配方/物料三页把搜索词/页码/排序存 sessionStorage，进详情返回后自动还原（批次/物料用 `restoreListState` + `savedSortOrder` + `useListState`；配方是客户端过滤，只需 `useListState({query})`）。**两条铁律**：① 搜索词必须在 `useServerPaging` **之前**铺回，否则它的 300ms 防抖 watcher 会把铺回当成一次"变化"、首屏多发一个相同参数的请求（实测 2 次）；② 排序必须作为 `useServerPaging` 的 **`initialSort/initialOrder` 构造参数**传入——`el-table` 的 `default-sort` 只在初始化读一次，构造后再改 ref 表格箭头不跟着走（表现为「页码回来了、排序退回默认」）。`useServerPaging` 的 `defaultSort` 现为 computed，与 `sort/order` 同源。
- **列表页状态用 sessionStorage 而非 URL query / keep-alive**：URL query 装不下搜索词且用户从侧栏回来不经 history 一样丢；keep-alive 整页缓存会让实时批次表看不到新批次。sessionStorage 随标签页生命周期，关标签即清。
- **单实例**：同一 DB 第二个进程拒启（退出码 75）；`dotnet run` 持有 bin DLL——重新 build 前先 `taskkill //F //IM RecipesManage.Api.exe`。
- **批次驱动 API**：POST /batches 需配方单元与设备类匹配（EQ_CLASS 校验，要遍历配方×设备组合）；`/start` 等动作 POST 需电子签名 `{ password }`，缺 body 会 400 "non-empty request body"。
- **事件存储**：四步握手事件落 `HandshakeEvents`（带 CreatedAt，Entity 基类），无导航属性到批表；批次状态历史无独立事件表，时间线类需求从 HandshakeEvents 取。
- **测试时序（已修复）**：Execution.Tests 曾有时序型 flaky 家族，根因是真实时钟窗口 + 测试集并行负载。已修：调度器与仿真器统一注入 TimeProvider（测试用 FakeTimeProvider + 后台推进器虚拟推时，窗口关键期 Pause 手动 Advance）、xunit.runner.json 关闭测试集并行。**新增调度器/引擎集成测试一律走 SchedulerHarness 的 fake 时钟模式**；hold/resume 编排类测试保持真实时钟（语义依赖实时观察顺序）。

## 验证方法（每页改后）

- 浏览器实测：playwright-core 脚本 + `NODE_PATH=<frontend>/node_modules NODE_OPTIONS= <managed node> script.mjs`；登录后可直接 `page.evaluate` 用 `localStorage.rm_token` 调 API 建数据。**NODE_OPTIONS 必须置空**，否则 SAFE_DELETE shim 会杀 Vite/pip。
- **playwright 登录等待别用 `waitForURL("**/dashboard")`**：登录页 URL 是 `/login?redirect=/dashboard`，该 glob 会被查询串命中而**立即返回**，此时尚未跳转、token 未写，后续导航全被守卫弹回 `/login`（症状是「所有角色所有页都 →/login」的假故障）。正确写法：`page.waitForFunction(() => location.pathname === "/dashboard" && !!localStorage.getItem("rm_token"))`。
- 脚本要放在 `frontend/` 内跑，放 `/tmp` 会 `ERR_MODULE_NOT_FOUND`（node_modules 解析不到）。
- vitest/vue-tsc 输出过管道 grep 会吞退出码——显式回显或别用管道判断成败。
- 写死阈值（如开关出现条件）无法触发时，临时 HMR 改阈值实测行为再还原，比只验证"隐藏"强。

## 运行总览（Dashboard）改进史

2026-10-03 一轮清零 P0-P2：语义着色、数据新鲜度徽标、占用行分层+只看非空闲开关（阈值 8 台）、KPI 脉冲、工步列、时长列（1s 时钟）、报警摘要条、积压老化（OldestPendingReleaseAt）、按角色过滤磁贴、执行态势卡换 2h 事件时间线（HandshakeEvents 白名单 Kind）。全站 15 页同日巡检过。
