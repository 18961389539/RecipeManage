# RecipesManage (BRMES) 项目长期约定

## 技术栈与运行
- .NET 10 后端（`RecipesManage.Api`）+ Vue3/Vite 前端，SQLite（`src/RecipesManage.Api/App_Data/recipes.db`），不用 Docker。
- 后端端口 **5010**（`launchSettings.json` 的 `applicationUrl` 为准，README 里的 5129 已过时）；前端 dev server `5173`，Vite 代理 `/api`、`/hubs`、`/health` → 5010。
- 演示账号：admin/Admin@123、engineer/Engineer@123、supervisor/Supervisor@123、qa/Quality@123、operator/Operator@123。
- 本仓库**已是 git 仓库**（分支 master，基线提交 `c105734`）。结构性改动前用 `git stash`/新分支保护即可；
  `.workbuddy/backup-20260920/` 是转 git 之前留下的历史备份，别再往那个目录打 tgz。

## 后端架构约定（2026-09-20 重构后）
- **批次/工步带 `ConcurrencyStamp` 乐观并发令牌**（`IConcurrencyStamped`，`AppDbContext` 在 4 个 SaveChanges 重载里轮换）。
  任何"HTTP 请求路径"与"后台调度线程"可能同时写的实体都要实现它；写批次状态一律走
  `BatchService.SaveBatchStateAsync`（→ DomainException "CONFLICT"）或调度侧的 `FlushAsync`（终态则收敛退出）。
- **车道相位属于 `batch_lanes` 表，不属于 production_batches**。每条 lane 独占一个 scope + DbContext，
  lane 之间不共享任何可变对象；**不要再引入共享 DbContext + 手写 SemaphoreSlim 那套 gate**。
  `production_batches.HandshakePhase` 只是汇总视图，**唯一生产者 = `BatchLanes.Format(lanes, fallback)`**
  （调度器 `MergedPhaseAsync` 调它，`FlushAsync` 统一落盘），读侧统一用 `BatchLanes.Parse`。
  以前这里还有一份读—改—写式的 `Merge(current, code, phase, multiLane)` + `ProductionBatch.UpdateLaneHandshake`
  （全仓无调用点，已删）——第二份拼串实现随时会被人重新用起来，等于第二个真源。
  UI 与集成测试都还在读这一列，改动时必须保持它准确，且 `Format → Parse` 必须能原样往返（有域测试钉住）。
- **设备占用 = `equipment_leases` 表的唯一索引**（一台设备一行），不是应用层查询。
  `EquipmentLeaseService.AcquireAsync/ReleaseAsync/ReconcileAsync`；**Faulted 也占用设备**（故障批次仍压在设备上，
  需操作员中止才释放），`OccupancyRealtime` 与租约策略口径保持一致。
- **数据修复只能执行一次**：`DataFixRunner` + `applied_data_fixes`。禁止再把 Repair* 挂到每次启动。
  **每一条对受控数据的机器改写都必须落审计**：修复函数把改动记进 `Trail`（按聚合根汇总，非 Draft 版本才进履历），
  由 `RunAsync` 与"已修过"标记**同一次 SaveChanges** 提交——修复函数自己不要再 `SaveChangesAsync`。
  审计 action 统一 `{聚合根}.autofix.{键}`（`recipe.` / `equipment.`），前端 `labels.ts` 的
  `auditActionDict` 必须同步加中文标签，否则履历页显示原始码。
  失败路径要 `db.ChangeTracker.Clear()`，否则半个修复会被下一条修复的提交一起写库、记到别人名下。
- **启动引导**：`SeedOptions(Demo, InitialPassword)`；`Seed:Demo` 默认 = `IsDevelopment()`，
  `Seed:AdminPassword` 未配且非 Demo 时随机生成口令并只打一次 warn。
  `SchemaBootstrap` 对老的 EnsureCreated 库**基线全部迁移**（那种库的模式就是当前模型）；
  只记第一条会让 `MigrateAsync` 把后续迁移对着已有模式重放、启动崩在半应用状态上。
- **加迁移时必须同时改 `AppDbContextModelSnapshot.cs`**，否则 SQLite 路径下 `SchemaBootstrap.ApplyAsync`
  直接抛 `PendingModelChangesWarning`（曾一次挂掉 18 个测试）。本机没有 `dotnet-ef`（restore 在沙箱不可用），迁移手写。
- **EF 的外键索引约定会连带改模型**：给某实体加一个"以 FK 列为最左前缀"的复合索引时，EF 会**移除**
  自动生成的单列 FK 索引。快照必须同步删那条 `b.HasIndex("Fk")`、迁移里也要真的 `DropIndex`，
  否则就是 `PendingModelChangesWarning`（2026-09-22 实测一次挂掉 20+ 集成测试）。
- **批次终态判据唯一来源 = `ProductionBatch.IsTerminalState(status)` / 实例 `.IsTerminal`**。
  调度器与 API 都从这里取，不要再写第二份状态名单。`Fault()` / `Complete()` / `Abort()` 都在域层拒绝覆盖终态
  （抛 `ALREADY_DONE`）；引擎的 `MarkFaultAsync` 撞终态时只打日志、不写库也不加报警行。
- **当前用户身份以数据库为准**：`Api/CurrentUserMiddleware` 在 `UseAuthentication` 之后按主键复核
  `IsActive` 与角色（停用即 401 `SESSION_INVALID`）。改角色/停用因此是即时生效，不等 JWT 过期。
  动 `ClaimsPrincipal` 时**不要新建 `ClaimsIdentity` 传字面量 nameType/roleType**——
  要沿用来源身份的 `identity.NameClaimType / RoleClaimType`，否则 `Identity.Name` 会退化成 GUID 并写进审计。
- **异常 → HTTP 状态码的唯一映射点 = `ExceptionHandlingMiddleware`**：`EQ_BUSY`→423、
  `CONFLICT`/`DUP_*`/`ALREADY_*`/`VERSION_MISMATCH`→409、`DbUpdateException`(UNIQUE)→409、
  其余 `DbUpdateException` 保持 500（NOT NULL/FK 违约是程序 bug，不要伪装成冲突）。新增 DomainException 码时改这里。
- **初始口令必须一账号一份**（`DatabaseSeeder.ResolveInitialPasswords`）：本系统电子签名=登录密码，
  共用口令会让三审链的"不同的人"失效。Demo 分支的五个公开演示口令是 e2e/README 的依赖，不要动。
  测口令等价性必须用 `hasher.Verify()`——bcrypt 每次加盐，比哈希会假通过。
- **只有 SQLite 一个提供方**：PostgreSQL 支持已于 2026-09 移除，新迁移**不需要**再写 Npgsql 分支
  （`Migrations/DualColumn.cs`、`launchSettings.json` 的 `http-postgresql` 等属待清理的死代码）。
  历史迁移里残留的 PG 分支不再执行，别去"修好"它。
- **授权策略唯一来源 = `Api/AuthorizationPolicies.cs`**（策略名按能力命名：`equipment.admin`、
  `quality.disposition`、`batch.skip`…，角色名单只在这个文件出现一次）。控制器写 `[Authorize(Policy = …)]`，
  Application 层原有的 `EnsureRole/EnsureExactRole` **保留当第二道**，不要因为加了策略就删。
  例外：`recipes/decide` 的合法角色取决于当前审核节点，只能由服务判，不要硬做成静态策略。
- **改授权后必须用五个角色各走一遍全站**（含详情页与各自可见的对话框），监听所有 `/api/` 响应看有没有 4xx。
  加策略最容易出的事故是挡住某个正常流程，而单测完全看不出来。
- **整库备份走 SQLite Backup API，不走文件复制**（WAL 下裸拷会得到"主库 + 半截 WAL"的撕裂快照），
  且必须 `POST` + Admin 策略 + 电子签名 + 审计。
  ⚠️ `Microsoft.Data.Sqlite` 默认连接池在 `Dispose` 后仍持有文件句柄，紧接着读该文件会 IOException——
  连接串要加 `Pooling=False` 并用嵌套 `using` 确保返回前释放。临时快照含口令哈希，用完删；删不掉要报错。
- **登录必须有失败限流与留痕**：`LoginGuard`（singleton，进程内计数，10 分钟 8 次 → `429 TOO_MANY_ATTEMPTS`）；
  `auth.login` / `auth.login.failed` / `auth.login.locked` 三类审计，**绝不记口令**；
  用户不存在与密码错返回同一句话，不要泄露"这个用户名有没有"。
- **名称类输入要过 `TextIntegrity.EnsureNotEncodingLoss`**（挂在 `MasterRecipe.Create` 与
  `RecipeTopology.ValidateNamedPhases`）：整串问号或连续 2 个以上问号 = 客户端代码页丢字，不可逆，只能入口拒绝。
  注意 `DataFixRunner` 的 `mojibake` 键只修相参数速率单位，不处理这种损坏，也别去改那个键名（改了会对所有已部署库重跑）。
- **不要把 `ex.Message` 原样回给客户端**（连接测试那类会带主机/端口/驱动细节）；按异常类型给可行动文案，细节进日志。

## 构建与测试（本机）
```bash
export APPDATA='C:\Users\35953\AppData\Roaming' ProgramData='C:\ProgramData' \
  ALLUSERSPROFILE='C:\ProgramData' USERPROFILE='C:\Users\35953' \
  MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 DOTNET_CLI_TELEMETRY_OPTOUT=1
"/c/Program Files/dotnet/dotnet.exe" build src/RecipesManage.Api/RecipesManage.Api.csproj -c Debug --no-restore
"/c/Program Files/dotnet/dotnet.exe" test RecipesManage.slnx -c Debug
```
- `dotnet test` 一次跑三个测试项目：Domain（域规则）、Execution（EF + 引擎集成）、
  **Contracts（`frontend/src/api/types.ts` ↔ `Dtos.cs` 契约守护）**。只跑单个 csproj 会漏掉契约检查。
- **契约测试的规则**：前端声明的每个字段后端必须真的返回（后端多返回一个前端不用的字段不算漂移）；
  枚举则要求逐值双向相等（标签表/颜色表/排序表都是按值手写的，加状态必须同步改前端）。
  改了 DTO 属性名或枚举成员而没改 `types.ts`，`dotnet test` 直接红——不要再靠人对齐。
  解析器不支持 `extends`，真用了它会抛而不是静默漏检。
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
  审计页的动作列同样查 `auditActionDict`——后端新增 action（含 `{聚合根}.autofix.{键}` 那批）要在这里加中文标签。
- **跨页共用的动作只允许一份实现**：配方包导入导出在 `src/api/recipesTransfer.ts`（失败提示、后端 messages 的展示、
  文件名日期戳都收在里面），审核决定权在 `src/utils/reviewGate.ts` 的 `canDecideReview`。
  两处此前各有副本且**行为已分叉**（一份把 messages 吞了、只剩"跳过 N"）。
  文件名日期一律 `format.formatFileDate()`，不要 `toISOString().slice(0,10)`——UTC 时差会把凌晨的备份写成前一天。
- **按钮可见性必须等于后端策略名单**：`/users` 页原先向 Admin 显示"导出/导入配方 JSON"，而
  `RecipeService.ExportAsync/ImportAsync` 都不放行 Admin，点了必然 403（该入口已删，配方包只在配方列表页传）。
  加按钮前先核对 `auth.can(...)` 与对应 `[Authorize(Policy=…)]` / `EnsureRole` 名单。
- 列表页统一要有：首屏 loading、empty-text、取数失败提示条（避免静默显示空/0 被误读为"真的没数据"）。
- **列排序唯一口径 = `src/utils/tableSort.ts`**（`byText/byNumber/byTime/byEnum` + `DESC_FIRST`），视图里不要写
  `a.x > b.x`。用 EP 自带 `sortable` + `:sort-method`（**不是 `sortable="custom"`**）：轮询换数组后 EP 仍保持排序，
  表头箭头与数据永远一致，代价是排序状态不进 URL。枚举列一律用 `labels.ts` 的 `xxxOrder` 次序数组（不是拼音序）；
  `DESC_FIRST` 只给时间/数量列，**枚举列用了会把"该先看谁"倒过来**；空值垫底只对升序成立
  （EP 的降序是把比较结果整体取反，实测降序时空值会翻到最前，两参数比较器拿不到方向）。
  服务端分页的表（审计页）不用客户端比较器：`sortable="custom"` + `SERVER_ORDERS`（去掉第三态）+ `serverSort(order, prop)`
  把 `{prop, order}` 翻成后端 `sort`/`dir` 参数，换排序键回第一页。
- **审计时间可在 SQL 侧排序**：`AuditLog.At` 在 `AppDbContext` 里挂了 `AuditTimestamp.Converter`
  （`yyyy-MM-dd HH:mm:ss.FFFFFFFzzz`，写入前 `ToUniversalTime()`）。SQLite 提供器禁止 ORDER BY 原生 DateTimeOffset 列，
  而这个写法字典序即时间序、且与历史行逐字符兼容，所以不用回填。**别把它换回本地时区**：偏移一变，
  `AuditTimestampTests` 与 `AuditQueryTests` 会红。审计的分页次级键固定是 `Id`，否则同秒同行会翻页重漏。
- **整行可点的表格必须接 `useKeyboardRows(tableRef, () => shown.value)`**：EP 的 `<tr>` 默认不可聚焦，
  键盘用户打不开任何详情。它在表格根元素上代理 Enter，**不要改成全局拦 Enter**
  （ShortcutHost 用 capture 监听 window，会先于按钮/表单收到事件，吞掉「刷新」按钮上的回车）。
  `is-dead` 行自动跳过。新增此类表格时记得给 `el-table` 加 `ref`。
- **写入表单用 `el-form` rules 就地校验，规则必须镜像后端已有校验**（如登录名≥3、密码≥8 来自
  `AuthService.CreateUserAsync`；重名/重复批号是 `DUP_*` 的前置版），不要自己发明强度策略。
  「要算才知道」的不兼容/占用原因仍走 `ElMessage.warning`。`rules` 用 `computed` 包（依赖列表数据或 form.id），
  重开对话框要 `nextTick(() => formRef.clearValidate())`，提交用 `validate().catch(() => false)`。
- **表格里空值一律裸「—」，不要套 `el-tag`**：灰色药丸里的短横与相邻列的裸短横是两种长相，读起来像控件坏了。
  写法 `<el-tag v-if="row.x">…</el-tag><span v-else>—</span>`。
- **顶栏实时徽标由路由决定**：只有 `meta.realtime` 的页面（dashboard / batches / batches/:id / alarms / equipment
  这 5 个真正 `useExecutionHub()` 的页面）才显示。新页面订阅 hub 就必须补这个 meta，否则用户会在不依赖推送的页
  看到红色「已断开」并当成系统故障。
- **`el-input-number` 放进 `el-table` 单元格必须 `width:100%`**：它默认 150px，会被窄列的 overflow 裁掉数字
  （看起来像空框）。全局口径：`.el-table :deep(.el-input-number) { width: 100% }`。
- **画布/卡片高度按内容算，不要写死**：Vue Flow 固定 480px 时三个工步下方空 300px 像没加载完；
  ECharts 窄卡里竖条的中文类名会被自动省略到只剩 3 个（改横向条，类名放 Y 轴）。
- **改 `@media print` 要同时覆写 EP 令牌和我们自己的文字令牌**：`.ebr{color:#111}` 只管继承，
  EP 表格单元格自带暗色浅字（实测白纸上 1.3:1）；表头色还走 `styles.css` 的 `var(--muted)`。
  验证用 `emulateMedia({media:'print'})` + `getComputedStyle`，别看截图（截图仍画背景，会误判）。
- **列表页统一取数反馈**：`src/utils/useLoad.ts` 的 `useLoad()` → `{ loading, error, run, runValue }`。
  新页面一律用它，不要手写 try/catch：loading 仅首屏为 true（轮询不闪 mask），error 用 el-alert 就地展示（不用 toast，轮询下会刷屏）。
  注意 `run(http.get<T>(...), d => (x.value = d))`：`http.get` 返回 AxiosResponse，run 内部已按 `.data` 解包，别再自己 `.data`。
  **走 `api/*.ts` 服务层的调用用 `runValue`**（服务层已经解包，返回 `Promise<T>`）；两者共用同一套 loading/error 语义，
  不要合并成一个函数去猜"返回值有没有 `.data` 属性"——DTO 自己也可能有个叫 data 的字段。
- **接口收口层 = `src/api/<域>.ts`**（已有 `equipment.ts`、`recipesTransfer.ts`）：URL 拼装与解包只写一次，
  失败一律 reject 交给调用方呈现。**不要在组件里引 store**（`http ← stores/auth ← router` 已成环，
  需要跨层拿值就用模块级 ref，见 `realtime/syncClock.ts`）。
- **拆巨型组件的口径**：先量"模板 vs 脚本"各占多少（本仓三个大文件重量都在脚本），
  再按"状态簇"抽 `utils/useXxx.ts` 或纯函数模块，最后才是子组件；只搬模板不算拆分。
  子组件的表单**只在打开时从 props 灌一次值**，不能持续跟随 props——列表每 4 秒重拉会把用户正在敲的输入冲掉。
  页面里 `useXxx()` 返回的 refs **必须解构成 setup 顶层绑定**再进模板：`feed.error.value` / `view.x.value`
  这种写法既难读又容易漏 `.value`（Vue 只自动解包顶层绑定）。
- **监控页的数据面 = `utils/useBatchFeed.ts`**：详情 + 四类附表（`load()` 已并行）+ `applyExecution(evt)`
  （实时事件打补丁，返回值表示"这类事件不带完整数据、必须重拉"）。六个签名动作共用一个 `runAction(name, fn)` 外壳
  （置忙→签名→提交→重拉→复位）；**取消签名时 esign 抛的是字符串 "cancel"**，只有这一处需要判断。
  趋势图的 ECharts/uPlot 实例、ResizeObserver 与卸载清理在 `components/BatchTrendCharts.vue`，
  对外只暴露 `refresh()`——`series` 是普通对象（不是 reactive），重绘必须显式触发。
- **四步握手进度文案 = `utils/handshakeProgress.ts`**（逐相位 → A/B/C/D 态，12 条单测钉住）。
  它不能并进 `utils/handshake.ts`：后者被 `labels.ts` 引用，反向引 labels 会成环。
- **前端单测**：`npm run test:unit`（vitest，配置挂在 `vite.config.ts` 的 `test` 段，`src/**/*.spec.ts`）。
  只给纯逻辑写（`utils/tagMap.ts` 那类"界面输入 → PLC 地址"的翻译点），组件渲染测试暂时不做。
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
  **徽标不能只报推送**：`src/realtime/syncClock.ts` 的 `lastSyncAt` 由 `api/http.ts` 在成功的 GET 上打点
  （写请求不打），徽标文案后面显示 `HH:mm:ss`，连丢 3 个轮询周期就转琥珀「刷新停滞」。
  它是模块级 `ref` 而不是 store 状态——`http.ts` 引 store 会把 `http ← stores/auth ← router` 的环再引一遍。
  徽标本身是 `<button>`，点击走 `usePolling` 的 `reloadRealtimeNow()` 立即重拉（在跑的轮询任务登记在一个 Set 里）。
  探活命令：`POST /hubs/execution/negotiate?negotiateVersion=1&access_token=<jwt>`（401=未带 token，200=正常）。
- **空数据用全局 `.none-note`（一行虚线说明），不要用 `el-empty`**：那枚 3D 插图约 250–300px 高，
  在要打印归档的记录里白占一页，在窄栏卡片里把内容挤出视野。打印色覆写留在各视图自己的 `@media print` 里。
- **对话框主按钮必须可见**：`styles.css` 已全局给 `.el-dialog` 设 `max-height` + `__body` 内滚 + `__footer` 固定。
  新增长表单对话框不用再做；觉得"保存不见了"先怀疑自己给 dialog 加了固定高度。
- **需要"原因 + 密码"的签名一律用 `esignWithReason()`（utils/esign.ts），一屏两栏**：
  不要再写 `prompt(原因)` → `esignPassword(密码)` 串联。`message` 必须传**函数** `() => h(...)`（静态 VNode 不重渲染，
  输入框打不出字）；`beforeClose` 里拦空值（不关窗）。只问密码的场景仍用 `esignPassword()`。
- **`ProcedureFlow` 的 `height` 是上限不是固定值**：组件内部按节点包围盒算 `canvasHeight`。
  调用方别再加写死的容器高度；节点少的时候画布就该矮。
- **下拉/选项文案按"决定能不能选"排序**（编码 → 不可选原因 → 空闲/占用 → 名称/类 → 协议）：
  窄控件必然截尾，把决策信息放前面，别把状态拼在长标签末尾。
- 视觉类改动先确认方向再落代码（用户偏好）。
- **跨组件的版式细节（卡片留白、表格表头、滚动条、提示条）一律写进 `src/styles.css` 全局块**，
  不要在各视图里重复写内联 style。压 Element Plus 组件样式优先用更具体的前缀选择器
  （如 `.aside .el-menu .el-menu-item.is-active`），避免 `!important`。

## 环境注意
- 本项目 dev server 必须用 `NODE_OPTIONS= npm run dev` 启动，否则 Vite 依赖重优化时会被沙箱 safe-delete 保护杀掉（详见用户级记忆）。
- `vue-tsc --noEmit` 是本项目唯一的类型校验手段（`vite build` 不做类型检查），改动后用 `NODE_OPTIONS= ./node_modules/.bin/vue-tsc --noEmit` 验证。
