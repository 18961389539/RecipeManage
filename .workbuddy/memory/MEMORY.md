# RecipesManage (BRMES) 项目长期约定

## 技术栈与运行
- .NET 10 后端（`RecipesManage.Api`）+ Vue3/Vite 前端，SQLite（`src/RecipesManage.Api/App_Data/recipes.db`），不用 Docker。
- 后端端口 **5010**（`launchSettings.json` 的 `applicationUrl` 为准，README 里的 5129 已过时）；前端 dev server `5173`，Vite 代理 `/api`、`/hubs`、`/health` → 5010。
- 演示账号：admin/Admin@123、engineer/Engineer@123、supervisor/Supervisor@123、qa/Quality@123、operator/Operator@123。
- **本仓库不是 git 仓库**（无 .git）。任何结构性改动前先在 `.workbuddy/backup-<日期>/` 打 tgz 备份（含 App_Data）。

## 后端架构约定（2026-09-20 重构后）
- **批次/工步带 `ConcurrencyStamp` 乐观并发令牌**（`IConcurrencyStamped`，`AppDbContext` 在 4 个 SaveChanges 重载里轮换）。
  任何"HTTP 请求路径"与"后台调度线程"可能同时写的实体都要实现它；写批次状态一律走
  `BatchService.SaveBatchStateAsync`（→ DomainException "CONFLICT"）或调度侧的 `FlushAsync`（终态则收敛退出）。
- **车道相位属于 `batch_lanes` 表，不属于 production_batches**。每条 lane 独占一个 scope + DbContext，
  lane 之间不共享任何可变对象；**不要再引入共享 DbContext + 手写 SemaphoreSlim 那套 gate**。
  `production_batches.HandshakePhase` 只是汇总视图（`MergedPhaseAsync` 计算后由 FlushAsync 统一落盘），
  UI 与集成测试都还在读它，改动时必须保持它准确。
- **设备占用 = `equipment_leases` 表的唯一索引**（一台设备一行），不是应用层查询。
  `EquipmentLeaseService.AcquireAsync/ReleaseAsync/ReconcileAsync`；**Faulted 也占用设备**（故障批次仍压在设备上，
  需操作员中止才释放），`OccupancyRealtime` 与租约策略口径保持一致。
- **数据修复只能执行一次**：`DataFixRunner` + `applied_data_fixes`。禁止再把 Repair* 挂到每次启动。
  涉及已发布配方的自动改动必须写审计。
- **启动引导**：`SeedOptions(Demo, InitialPassword)`；`Seed:Demo` 默认 = `IsDevelopment()`，
  `Seed:AdminPassword` 未配且非 Demo 时随机生成口令并只打一次 warn。
- **加迁移时必须同时改 `AppDbContextModelSnapshot.cs`**，否则 SQLite 路径下 `SchemaBootstrap.ApplyAsync`
  直接抛 `PendingModelChangesWarning`（曾一次挂掉 18 个测试）。本机没有 `dotnet-ef`（restore 在沙箱不可用），迁移手写。
- 迁移需同时覆盖 SQLite 与 Npgsql：用 `DualColumn.For(migrationBuilder)` 取类型，分支用 `DualColumn.IsNpgsql`。
- PG 相关测试在本机跑不起来：`%TEMP%\brmes-pg-embed\...\bin` 只有 `postgres.exe`、缺 `pg_ctl`。属环境问题，非回归。

## 构建与测试（本机）
```bash
export APPDATA='C:\Users\35953\AppData\Roaming' ProgramData='C:\ProgramData' \
  ALLUSERSPROFILE='C:\ProgramData' USERPROFILE='C:\Users\35953' \
  MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 DOTNET_CLI_TELEMETRY_OPTOUT=1
"/c/Program Files/dotnet/dotnet.exe" build src/RecipesManage.Api/RecipesManage.Api.csproj -c Debug --no-restore
"/c/Program Files/dotnet/dotnet.exe" test tests/RecipesManage.Execution.Tests/... -c Debug --no-restore
```
- 缺少这些 env 会报 `NETSDK1060 ... Value cannot be null (Parameter 'path1')`。
- 有 API 实例在跑时编译会因 dll 被锁报 MSB3021/3027（假失败），先停进程再 build。

## 前端 UI 约定
- **色板唯一数据源 = `src/styles.css` 的 `:root`**（表面四档 `--sunken/--panel/--raised/--hover/--tint`、
  文字 `--text/--text-body/--muted/--idle`、状态 `--accent/--ok/--warn/--cool/--violet`、`--accent-rgb` 供斜杠透明度）。
  组件里一律 `var(--x)`，**不要再落 hex 字面量**；Element Plus 的配色在 `html.dark {}` 覆盖块里引用这些令牌。
  画布类（ECharts/uPlot）读不了 `var()`，改用 `src/utils/theme.ts` 的 `palette('--token')` 运行时取值。
- 新增 Element Plus 组件的配色需求**一律改 `html.dark {}` 覆盖块**，不要在组件里硬编码颜色。
- **改状态色必须整条阶梯一起改**（`--el-color-xx-light-3/5/7/8/9` + `dark-2`），漏一档那个交互态就跳回 EP 默认色。
  暗色主题下 `light-N` 是**与 #141414 混合（越 light 越暗）**，`dark-2` 才往白混——照抄浅色主题取值会把
  `primary-light-9` 写成近白，而 el-table 当前行底色正是它（实测踩过 1.32:1）。
- **亮底一律压深色墨水 `var(--bg)`，不压白字**：本项目状态色偏亮，白字在 12px 上只有 1.6~3.3:1。
  已在 styles.css 收口：el-tag--dark、el-radio-button 选中态、五种 type 的实色按钮（含 hover 用
  `color-mix` 往亮提亮、`:not(.is-plain):not(.is-text):not(.is-link)` 必须留着，否则 plain/link 主色文字会隐形）。
  压过 EP 用 `html.dark .xxx` 前缀抬特异性即可，**不要用 !important**。禁用态按 WCAG 1.4.3 豁免，不为它调对比度。
- **枚举中文化/配色唯一数据源**：`src/utils/labels.ts`。新页面不要自己写 statusLabel，从这里导入
  （建议沿用别名导入 `xxxLabel as statusLabel` 的做法以保持调用点稳定）。
- 列表页统一要有：首屏 loading、empty-text、取数失败提示条（避免静默显示空/0 被误读为"真的没数据"）。
- **列表页统一取数反馈**：`src/utils/useLoad.ts` 的 `useLoad()` → `{ loading, error, run }`。
  新页面一律用它，不要手写 try/catch：loading 仅首屏为 true（轮询不闪 mask），error 用 el-alert 就地展示（不用 toast，轮询下会刷屏）。
  注意 `run(http.get<T>(...), d => (x.value = d))`：`http.get` 返回 AxiosResponse，run 内部已按 `.data` 解包，别再自己 `.data`。
- **时间/数字格式化**：`src/utils/format.ts` 的 `formatDateTime/formatDate/formatNumber`。禁止把 ISO 串直接甩给 UI。
- **术语解释（tooltip）**：`src/utils/glossary.ts` 字典 + `src/components/HelpTip.vue` 组件。
  新术语一律加进 glossary，**key 用界面渲染的中文**（与 labels.ts 输出一致），这样可直接用标签值反查，
  典型用法 `<HelpTip :term="handshakePhaseLabel(row.x)">{{ handshakePhaseLabel(row.x) }}</HelpTip>`。
  HelpTip 对未收录的 key 自动降级为普通文本，不会出现空气泡。
  气泡样式只能是全局 `.help-tip-pop`（含 white-space: pre-line）——el-tooltip 气泡渲染到 body 下，scoped 无效。
- 写入类操作（保存/创建/登记/拆分）必须给按钮加 `:loading`，防止重复提交。
- **实时连接状态**：`src/stores/realtime.ts` + `src/components/RealtimeStatus.vue`（顶栏徽标）。
  任何依赖 SignalR 的新页面都走 `useExecutionHub()`，它已自动上报连接状态，不要再自己建连接。
  监控类页面不要只靠轮询就宣称数据是实时的——连接断开时必须让用户看得见。
  探活命令：`POST /hubs/execution/negotiate?negotiateVersion=1&access_token=<jwt>`（401=未带 token，200=正常）。
- 视觉类改动先确认方向再落代码（用户偏好）。
- **跨组件的版式细节（卡片留白、表格表头、滚动条、提示条）一律写进 `src/styles.css` 全局块**，
  不要在各视图里重复写内联 style。压 Element Plus 组件样式优先用更具体的前缀选择器
  （如 `.aside .el-menu .el-menu-item.is-active`），避免 `!important`。

## 环境注意
- 本项目 dev server 必须用 `NODE_OPTIONS= npm run dev` 启动，否则 Vite 依赖重优化时会被沙箱 safe-delete 保护杀掉（详见用户级记忆）。
- `vue-tsc --noEmit` 是本项目唯一的类型校验手段（`vite build` 不做类型检查），改动后用 `NODE_OPTIONS= ./node_modules/.bin/vue-tsc --noEmit` 验证。
