# BRMES / RecipesManage 架构改进计划

> 状态：已拟定，待按阶段实施  
> 日期：2026-09-22  
> 范围：本地仓库 `D:\SourceCode\RecipesManage`（当前无远程）

## 目标与原则

**目标**

- 生产环境可关闭仿真 PLC，避免误占端口与假设备。
- 授权规则单一可审计（Policy / Handler），与电子签名、业务状态机职责分开。
- 批次 / 配方 Application 服务可维护，避免继续堆进 God Service。
- 调度「单机 + SQLite」边界写清楚，进程重启后意图可恢复。

**原则**

- 小步可回滚；先开关与测试，再挪逻辑。
- 不推倒握手状态机（`HandshakeStateMachine`）；宜加测试与观测。
- 不引入微服务、消息队列，也不为「干净架构」强行上 MediatR / 全套仓储。
- 单阶段可单独合并；不必一次做完。

**改前基线**

- 在开发机跑通 Domain 相关测试，以及关键冒烟：登录 → 开批 → start → 监控页连上 SignalR Hub。
- 建议命令（按仓库习惯调整）：
  - `dotnet test tests/RecipesManage.Domain.Tests`
  - `dotnet test tests/RecipesManage.Execution.Tests`（按改动范围）
  - 前端若动权限：相关 Playwright e2e 或手工角色矩阵

---

## 阶段 0：基线与约束（约 0.5 天）

### 做什么

- 新增短 ADR，例如 `docs/adr/0001-runtime-boundaries.md`，写明：
  - 单进程部署
  - 数据库为 SQLite（`RecipesDatabase` 已定调；PostgreSQL 支持已移除）
  - 调度使用内存 `Channel` + 单读者，不支持多实例水平扩展
  - 正式环境默认禁用仿真 PLC
- 可选：在 `README.md` 增加「架构约束」小节，链到 ADR。
- 记录当前测试命令与通过情况，便于回归对比。

### 涉及

- 新建 `docs/adr/…`
- 可选修改 `README.md`

### 验收

- 新人只看 ADR 能回答：能否多实例？用什么库？仿真何时启用？

---

## 阶段 1：仿真 PLC 可关（约 1 天，优先）

### 做什么

1. 增加配置项，例如 `Plc:EnableSimulators`：
   - Development 默认 `true`
   - 其它环境默认 `false`
2. 在 `ServiceCollectionExtensions` 中仅当开关为真时注册：
   - `ModbusLoopbackHostedService` / `OpcUaLoopbackHostedService` / `SiemensS7LoopbackHostedService`
   - 仅仿真使用的 Slave / Rack 单例
3. 与种子对齐：`DatabaseSeeder` 在非 Demo / 非仿真时不写入 `MB-01` 等仿真设备（或与现有 `Seed:Demo` 明确绑定），避免「关了仿真库里还有假设备」。
4. README 写明如何开启仿真及默认端口（如 Modbus `1502`）。

### 涉及（预期）

- `src/RecipesManage.Api/Hosting/ServiceCollectionExtensions.cs`
- `appsettings.json` / `appsettings.Development.json`
- `src/RecipesManage.Infrastructure/Persistence/DatabaseSeeder.cs`（若与仿真设备绑定）
- `README.md`
- 可选：小型宿主测试或手册验收清单

### 验收

- Development + 开开关：演示配方仍能跑通握手。
- 关开关：无仿真端口监听，日志无 loopback 启动；`IPlcDriverFactory` 仍可解析真实驱动。
- 生产配置缺省为关，不会误开。

### 风险

- 种子数据与仿真设备码耦合 → 种子与开关一起改，或文档写清关仿真时使用哪台设备。

---

## 阶段 2：授权单轨化（约 2–3 天）

### 背景

- 已有 `AuthorizationPolicies`，部分控制器已挂 Policy。
- `BatchService` 等仍通过 `EsignGuard` / `EnsureRole` 做第二道角色判断。
- `RecipesController` 的 `decide` 因审批节点动态，刻意未加 Policy，逻辑在服务层。

### 做什么

1. 盘点全部 `EnsureRole` / `EnsureExactRole` 与控制器 `[Authorize(Policy=…)]` 的对应关系。
2. 写操作以 Policy 为准；服务层去掉「纯角色」检查，**保留**电子签名、批次状态、审批节点等业务校验。
3. 补齐缺口：
   - `decide`：动态 `IAuthorizationHandler`（按当前审批级 Supervisor / Quality），或拆成明确 endpoint + Policy。
   - 类级仅有 `[Authorize]`、写操作靠服务内角色判断的接口，补上 Policy。
4. 维护 `docs/auth-matrix.md`：路由 `meta.roles` 与后端 Policy 对照；改角色先改表再改代码。

### 涉及（预期）

- `src/RecipesManage.Api/AuthorizationPolicies.cs`（及新 Handler）
- `BatchesController` / `RecipesController` / `EquipmentController` 等
- `BatchService`、`RecipeService`、`EsignGuard`（收窄职责）
- 授权相关单测或集成测试
- `docs/auth-matrix.md`

### 验收

- 演示角色矩阵：工程师不能 start；操作员不能 skip；仅质量可 disposition；未到节点的 `decide` 返回 403。
- 服务层不再出现「仅角色」的 `EnsureRole`（签名校验除外）。
- 从控制器 / OpenAPI 能看出写操作所需 Policy。

### 风险

- `decide` 动态角色最易回归 → 先补测试再删服务内角色判断。

---

## 阶段 3：拆分过胖的 Application 服务（约 3–5 天，可切片合并）

### 背景

- `IAppDbContext` 暴露全套 `DbSet<>`；Application 引用 EF Core。
- `BatchService` 约 660 行，`RecipeService` 约 330 行，含大量 `Include` / `SaveChanges`。

### 做什么（每片可单独 PR）

1. **批次写路径**：`BatchLifecycleService`（create / start / abort / hold / resume / skip / confirm）。
2. **批次读路径**：`BatchQueryService`（列表、详情、样品、握手日志、报警）。
3. **质量路径**：release / reject-disposition / lab-samples → `BatchQualityService`。
4. **配方**：编辑（procedure / header）与审批流（submit / decide / reopen / new-version）分开。
5. 控制器只依赖窄接口；本阶段仍可用 `IAppDbContext`，不强制完整仓储。
6. 每拆一片同步挪测试或补最小用例。

### 涉及

- `src/RecipesManage.Application/Services/BatchService.cs`
- `src/RecipesManage.Application/Services/RecipeService.cs`
- 对应 Controller 构造注入
- `tests/…`

### 验收

- 单文件显著变短（写路径目标约 &lt; 250 行量级）；行为与拆前测试一致。
- 不新增无调用方的抽象层。

### 暂缓

- 全面仓储、MediatR、读写完全 CQRS。

---

## 阶段 4：调度意图落库与可恢复性（约 2–3 天）

### 背景

- `BatchSchedulerHostedService`：内存 `Channel` + `ConcurrentDictionary` 会话 / Hold / Skip / Confirm。
- 已有 `RecoverRunningAsync` 与 `SchedulerIntent` 实体，可作为权威命令日志的基础。
- 代码注释已说明按 SQLite 单进程设计。

### 做什么

1. 盘点内存态与 `SchedulerIntent` 的覆盖差距。
2. Hold / Skip / Confirm / Abort 等写入意图表（或扩展现有表）；调度以库为准消费。
3. 增强恢复：重启后不仅拉起 Running，也能恢复未完成意图。
4. ADR 补充：仍不支持多实例；本阶段只保证同机进程重启可恢复。

### 涉及

- `BatchSchedulerHostedService.cs` / `BatchSchedulerHostedService.Waves.cs`
- `SchedulerIntent` + 可能的 EF migration
- `BatchService`（或生命周期服务）入队处

### 验收

- Running 中杀进程再启动：会话恢复；不丢当前步、不重复盲写（与握手 / 快照一致）。
- 重启前已发的 Hold / Skip 可恢复或可明确重放。
- 测试覆盖「写意图 → 重启 → 恢复」。

### 风险

- Waves（并行车道）复杂：先落命令意图，不重写并行编排。`BatchSchedulerHostedService.Waves.cs` 逾千行，编排与单车道握手宜后续再拆。

---

## 阶段 5：巩固项（穿插，各项约 0.5–1 天）

| 项 | 做法 | 验收 |
| --- | --- | --- |
| 快照 schema 版本 | 控制配方 JSON 增加 `schemaVersion`；读时按版本解释 | 旧批次记录仍可打开 |
| JWT / 密钥 | 开发密钥仅 Development；禁止提交生产密钥 | 非 Dev 缺/弱密钥无法启动（补测试） |
| 前端实时 | Hub 订阅 / 退订与批次页生命周期明确 | 多标签切换不串批 |
| 授权矩阵文档 | `docs/auth-matrix.md` 与代码同步 | 改角色先改表 |
| Waves 可读性 | 「编排」与「单车道握手」拆文件/类型 | 单测不减、认知负担下降 |

---

## 建议排期

### 单人全职约 2 周

| 周次 | 内容 |
| --- | --- |
| 第 1 周 | 阶段 0 → 1 → 2（约束文档 + 仿真开关 + 授权单轨） |
| 第 2 周 | 阶段 3 前两片（批次写/读拆分）+ 阶段 4 启动 |
| 之后 | 阶段 3 剩余切片 + 阶段 5 |

### 若只有约 3 天

只做：阶段 0 + 阶段 1 + 阶段 2 的「盘点 + decide Handler + 仿真开关」。阶段 3 / 4 另排。

---

## 每阶段共用工程习惯

- 一阶段一分支（或一批清晰提交）；说明「为什么」。
- 合并前：相关 `dotnet test`；若动前端权限则跑对应 e2e 或手工矩阵。
- 不改握手核心路径，除非测试先红再改。
- 本仓库的 `origin` 是同盘裸镜像 `D:\SourceCode\RecipesManage-remote.git`（不是网络远端），合并策略以本地分支 /
  该镜像为准；若日后加真远端，再按 PR 流程执行。

---

## 明确不做（近期）

- 为扩展而换 PostgreSQL 或上消息队列（未证明单机瓶颈前）。
- 整仓微服务拆分。
- 重写 `HandshakeStateMachine`。

---

## 推荐实施顺序（三个迭代）

1. **阶段 1**：仿真 PLC 配置开关 + 生产禁用。  
2. **阶段 2**：授权单轨化（Policy / Handler），削掉散落的纯角色 `EnsureRole`。  
3. **阶段 3 前两片 + 阶段 4 启动**：拆 `BatchService` 写/读，调度意图落库。

---

## 进度跟踪

| 阶段 | 状态 | 备注 |
| --- | --- | --- |
| 0 基线与 ADR | 待开始 | 2026-10-03 核对：`docs/adr/` 仍不存在。"单进程 / 单实例"这一条已在 `docs/deployment.md` §2 写明并有代码强制（`SingleInstanceLock`），"SQLite、不支持水平扩展、仿真默认关"散落在 README / 部署文档 / 类注释里，没有一份 ADR 把它们收拢——新人答"能否多实例"要翻三处。 |
| 1 仿真 PLC 可关 | **已完成（2026-09-25，做法与计划不同）** | 没有加 `Plc:EnableSimulators` 配置开关，而是按**库里的设备行**判：`MB-01`/`S7-01`/`UA-01` 存在、启用、协议+端口对得上且 host 是回环才绑端口（`Simulation/PlcLoopbackGate.cs`；2026-10-02 起仿真独立成 `RecipesManage.Simulation` 项目）。理由：设备行是这台机器"要连什么"的唯一真源，再加一个配置项就会有两份真源（配置说开、库说没有 → 端口白占）。`Seed:Demo=false` 时那三条行本来就不写，所以生产机零占用；判据只在启动时跑一次。 |
| 2 授权单轨化 | **部分完成（2026-10-02，做法与计划不同）** | 没有按计划"服务层去掉纯角色检查"：电子签名路径需要纵深防御，服务层被别处直接调用时也必须自保。改成**名单单轨**——14 项能力的角色名单只在 `Domain/Identity/Capabilities.cs` 写一次；`AuthorizationPolicies` 由它生成，服务层统一 `EnsureCan(Capabilities.X)`（`CapabilityExtensions`），约 30 处手写角色列表清零。这不是洁癖：注入仿真故障的策略放行 Admin 而服务层拒绝 Admin，已经漂移过；现拆成独立能力 `equipment.simulate`。`CapabilitiesTests` 把"角色 × 能力"矩阵与职责分离规则（仅质量可放行、仅主管可跳步、Admin 不碰产线）钉成测试。另补了 `ValidateTagMap`/`TestConnection` 缺失的服务层二次校验。**未做**：`docs/auth-matrix.md`。`recipes/{id}/decide` 的动态节点授权有意留在 `RecipeApprovalService`（按审批节点的 `RequiredRole` 判，运行时才定，放不进静态能力表）。 |
| 2b 电子签名结构化 | **已完成（2026-10-02）** | 批次/化验签名从"audit_logs 里一行 含义+备注 的拼接文本"变成表 `signature_records`：**含义原文在签署时冻结**，备注单独成列；批记录读库里的文本，不再用当前代码的含义表反查（以前改一次措辞，历史批记录展示的就不是当时签的那句，不满足 21 CFR 11.50）。迁移 `20261002130000_SignatureRecords` 回填历史 `*.esign` 审计行：已知措辞拆成含义+备注，更早的措辞整段原样留作含义。写入统一经 `EsignGuard.Record`，同时保留审计行。**未做**：签名与被签对象内容哈希的绑定；配方侧签名（审核节点含义已冻在 `approval_records`，提交/升版等仍只在审计日志）。 |
| 3 拆分 Application 服务 | **部分完成（2026-10-02）** | `BatchService`（原 ~930 行）按读写拆开：读路径（列表 / 详情 / 趋势 / 握手履历 / 快照漂移 / 批记录 / PDF / 报警列表）→ `BatchQueryService`，只依赖数据库、当前用户、物料服务、PDF 渲染器；`BatchService` 只剩写路径（创建 / 启动 / 中止 / 保持 / 恢复 / 跳步 / 确认 / 放行 / 拒收 / 报警确认），返回详情时委托查询服务，反向无依赖。`EsignGuard` 改为容器注入（`BatchService` / `RecipeService` / `MaterialLotService` / `ApprovalChainService` 四个服务都不再自己 `new`，`ArchitectureBoundaryTests` 用 Theory 钉住）。跳步规则下沉到领域 `Domain/Batches/StepSkip`：目标解析（`ResolveTarget`）、放行判定矩阵（`Decide`：转发给握手引擎 / 离线直接应用 / 拒绝并带错误码）、离线状态迁移（`ApplyOffline`）都是纯函数，`StepSkipTests` 覆盖每个拒绝码与放行路径；`BatchService.SkipAsync` 只剩装载、落库、审计、发布。`ArchitectureBoundaryTests` 钉住读路径不得依赖调度器 / 租约 / 密码校验。`RecipeService`（原 ~390 行）按职责拆成四个：编辑 `RecipeService`（建 / 改表头 / 保存工艺 / 升版）、读 `RecipeQueryService`（列表 / 详情 / 版本对比，构造函数只有数据库）、审批流 `RecipeApprovalService`（提交 / 审核 / 驳回重开 / 指定审批链，含按节点判定的 `decide` 授权）、导入导出 `RecipePackageService`；装载、映射、审计这几件无状态的事放进内部静态类 `RecipeSupport`，顺带去掉了 `BatchQueryService` 对 `RecipeService` 的依赖。`ArchitectureBoundaryTests` 钉住查询服务只依赖数据库、四个服务互不持有。`recipes/{id}/decide` 的动态节点授权留在 `RecipeApprovalService`（按审批节点的 `RequiredRole` 判断，不属于静态能力表，这是有意的）。**未做**：放行 / 拒收没有单独成类（两个入口 ~45 行，拆出来只会多一份共享依赖）。 |
| 4 调度意图落库 | **大部分完成（2026-10-03 核对；实现早于本行更新）** | 盘点结论：Hold / Skip / Confirm 三类操作员指令已全部落 `scheduler_intents`（迁移 `20260921100000_SchedulerIntents`，2026-10-02 起按工步分行 `SchedulerIntentPerStep`）。`BatchService` 在**入队之前**先把意图随批次状态同一次落库；调度器启动时 `RecoverRunningAsync` 先把库里全部意图装回内存，再拉起 Running / Queued 批次，每次 `StartBatch` 前再 `HydrateIntents` 一次；意图的消费与工步结论**同一次提交**里删除，冲突重载不会让已消费的意图复活（否则重启后会把执行过的跳步 / 确认再执行一遍）。Abort 有意不做成意图：批次先定稿为 `Aborted` 再入队，终态本身就是权威记录；租约由启动时的对账（`EquipmentLeaseService`）清掉失效的。同机重启的单实例约束写在 `docs/deployment.md` §2，由 `SingleInstanceLock` 强制。验收覆盖：运行中重启不重写已完成的升温 / 不重发 PLC 载荷（2 个）、Hold 重放、**Confirm 重放、Skip 重放（2026-10-03 补）**。**未做 / 未验证**：ADR 补充（`docs/adr/` 不存在，见阶段 0）；"调度以库为准消费"没有做——命令仍由进程内通道驱动，库是重放日志，这是取舍而非缺口；崩溃落在"批次已定稿 Aborted、调度器还没复位设备"之间时，重启只对账租约、不会补发设备复位（这点仍成立）。**但它的后果已堵住（2026-10-03）**：核对时发现真正的危险不在"设备没复位"，而在下一个批次——会话的第一个工步无条件"从 PLC 当前相位续跑"，设备上残留的 Step_Complete 会让新工步直接进归档、残留 Step_Running 会让它空等别人的时长，两种都会在**一次参数都没写给 PLC** 的情况下把工步记成完成（批记录上还带归档实测）。残留来源不止崩溃：中止时 PLC 连不上、复位被吞掉而租约照放；崩在"工步记完成"与"复位 Step_Complete"之间（每步 100ms 量级的窗口）；现场手动动过设备。两个新测试（`StaleHandshakeTests`）先复现了（一个"没写参数就完成"，一个空等 120s），现改为：续跑只在上位机记得这一步发过指令时成立（进入时工步已是 Running / Held，`MarkStarted` 在写 PLC 之前落库）；进入时工步仍是 Pending 却见到 Running / Complete / Trigger 回显，一律视为残留，先复位再按全新工步握手，并留一条 `reset` 履历和一条警告日志；复位后设备仍不空闲就由状态机的 `PlcNotIdle` 拒绝写入并报故障。**仍未做**：启动时不会主动去复位"孤儿"设备（没有持久化的"待复位"标记），设备要等下一个批次占用它时才被清。 |
| 4b SQLite 连接参数 | **已完成（2026-10-03，由追偶发失败发现）** | 新连接的 `busy_timeout` 默认是 **0**：别的连接正在写时立刻报 `database is locked`，不等待；`Default Timeout=180` 试过，完全没用（那只管语句级重试，管不到提交阶段）。引擎测试在 12 路并行压力下约 6% 的次数把一个所有工步已完成的批次置成 `Faulted(ENGINE)`——即**生产里同样会发生**：调度车道、HTTP、备份、维护各自开连接写同一个文件。现由 `SqliteConnectionPragmas`（连接拦截器）在每条连接打开时设 `busy_timeout=10000`，挂在唯一的生产配置入口 `RecipesDatabase.Apply` 上；引擎测试宿主改走同一入口，不再自己 `UseSqlite`。修后同样压力 96 轮 × 3 个用例 0 失败（修前约 6%）。同时 `ENGINE` 故障消息带上最内层原因（`DescribeFault`）：此前只有 EF 的外壳 "An error occurred while saving the entity changes"，现场根本看不出是锁。**另：WAL 模式补上（同日）**——此前代码里没有任何地方设 `journal_mode=WAL`，而 `DatabaseBackup` / `RecipesDatabase` 的注释都写"库跑在 WAL 模式下"：EF 只在**它自己创建库文件**时才会切 WAL，而 `SchemaBootstrap` 先探表（探表本身就建出了空文件），所以新装机的库实测是 `delete`（回滚日志）模式，只有历史遗留的开发库是 WAL。现由 `SqliteJournal.EnsureWalAsync` 在 `SchemaBootstrap.ApplyAsync` 迁移**之后**切换（升级快照与迁移仍在原模式下进行；模式写进库文件，只需一次，旧装机自动转换）；`synchronous` 保持默认 `FULL`，不降到 `NORMAL`（电子签名/审计数据，宁可慢也不能在掉电时丢最后几次提交）。配套：VACUUM 之后立即 `wal_checkpoint(TRUNCATE)`，避免 `-wal` 涨到与库同大；备份产物仍是单个自包含文件（`WalModeTests` 覆盖：新库为 WAL、旧回滚模式库转换不丢数据、WAL 库的备份无 `-wal`/`-shm` 且含最新提交、VACUUM 后 `-wal` 为 0 字节——后者做过变异验证）；`docs/deployment.md` §2.1 写明三个文件的含义、禁止网络盘/同步盘、手动拷库的正确做法，§8 恢复演练补上"同时移走旧的 `-wal`/`-shm`"。实机起 Api 验证：新库启动后 `recipes.db-wal` / `-shm` 出现；WAL 下同样压力 96 轮 × 3 个用例 0 失败。**未做**：系统里没有"恢复"代码，恢复只是 §8 的手工流程，所以"清残留 sidecar"只能靠手册，没有程序保证。 |
| 5 巩固项 | 部分完成 | JWT/密钥一项已做（非 Dev 缺/弱占位密钥直接拒绝启动，实测）；其余待开始 |

---

## 面向更多客户（2026-09-30 补）

客户数从 1 变成 N 时最先断的不是功能而是**运维可见性**，所以这一批做的是"现场能自证 + 出事能回退"：

| 做 | 落点 |
| --- | --- |
| 版本与库结构水位可被念出来 | `Directory.Build.props` 定 `Version`；出包脚本写 `InformationalVersion=版本+短提交号`；`/health` 回 `version` / `migration` / `pendingMigrations`；运行总览徽标悬停显示，e2e 钉两端 |
| 日志落盘、可按天带回 | `Infrastructure/Diagnostics/RollingFileLoggerProvider.cs`（自写，不引框架）：`App_Data/logs/brmes-<UTC日期>.log`，默认不删旧日志，写不进磁盘只停这道 sink、绝不拖停宿主 |
| 升级前先有一份能开的库 | `SchemaBootstrap` 在有 pending 迁移且库里已有表时，先落**已校验**快照到 `backups/pre-migration/`；快照写不成就不迁移。`Down()` 无调用路径，回滚 = 恢复该快照 |
| 网络抖动不再吞批次 | `Application/Services/PlcConnectRetry.cs`：**只重试连接**（幂等），默认 4 次 / 累计 ≤2.3s；写与触发一律不重试 |
| 出包与安装分离 | `deploy/publish-package.ps1`（开发机出 zip + manifest + sha256）；`install-watchdog.ps1 -PackagePath`（现场只解包，`-Clean` 才删旧文件）——修掉了"现场不需要 SDK"却在现场 `dotnet publish` 的自相矛盾 |
| 两三人小厂的默认取向 | 预置链 `single-review`（提交 + 一道质量签核，`IsDefault=false`）；`appsettings.Production.json` 模板给 `Jwt:ExpireHours=8`。职责分离规则一行未松 |
| 归档必须有实测来源 | `QualityArchive.ResolveSourceTag` 成为唯一口径（声明优先，其次 Duration/温度/压力推断），`DemandArchivableSources` 在**提交审核**就拒绝没有来源的归档规格，开批时还要求该键存在于设备点表。理由不是"不优雅"：以前它一路静默到放行——`HasOutOfSpec` 把"从未取到值"算成超差，而偏差放行**只要求填一句话**，于是库里三条已放行批次带着"归档质检合格"的意见过去了，履历上看不出那条规格从没被评价过。实验室量（硬度）改建质检样品 |
| 参数语义加 `MeasuredValue` | 声明"这列的是被测质量特性"就必须同时声明实测点；前端 `measuredTagRequired()` 单一判断，设计器与相库共用。**没有加 `Quantity`**：配比还没有消费方，别造空转枚举 |
| 时长不再被静默改写 | `ProcessDuration` 认 ms/s/min/h/d（识别表与换算表同源），写 PLC 时长槽的窗口 `0.2s–7200s` 提成常量并**超窗口报错**（`DURATION_RANGE`）。过去 `Math.Clamp` 把 24 小时固化写成 2 小时发给 PLC，而上位机仍按原值等。Submit 逐参数校验（槽 15 上的显式时长不经过那条合成路径） |

**没做，且需要单独一轮**：工步进行中读失败的容忍（只重连不补写，仍会把一次 8s 超时算成故障）——它直接压在
`禁止盲写` 的边界上，必须配独立的引擎测试再做；SQLite 写侧合批（多设备订单真来了再说）；多站点/租户（建议用
"每站点一套独立部署"绕开，见 [[brmes-single-machine-gaps]] 的判据）。

---

## 修订记录

| 日期 | 说明 |
| --- | --- |
| 2026-09-22 | 初稿：基于当前 slnx 分层、DI、调度器与授权现状整理 |
| 2026-09-30 | 阶段 1 完成（做法改为按设备行判定，见上表）；补「面向更多客户」一节记录 A 档六项与其未做部分；`origin` 已是同盘镜像而非"无远程" |
| 2026-10-03 | 核对阶段 4：Hold / Skip / Confirm 意图落库与重启恢复早已实现，补 Confirm / Skip 重放测试；追引擎测试偶发失败，定位为连接 `busy_timeout=0`（新增阶段 4b），并发现代码从未设置 WAL |
| 2026-10-03 | 强制 SQLite WAL 模式（`SqliteJournal`，迁移后切换；`synchronous` 保持 FULL）；VACUUM 后截断检查点；`deployment.md` 补 §2.1 与恢复演练的 sidecar 清理；新增 `WalModeTests` |
| 2026-10-03 | 堵住"设备残留握手位把新工步带着记完成"：工步进入时仍是 Pending 却见残留 Running / Complete / Trigger 回显，先复位再按全新工步握手；新增 `StaleHandshakeTests`（先红后绿） |
