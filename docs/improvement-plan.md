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
| 快照 schema 版本 ✅ | 控制配方 JSON 增加 `schemaVersion`（`SnapshotSchema`，当前 1，旧批次缺省为 0）；版本 ≥1 的哈希把版本号纳入（V3）且不退回旧口径；比本程序新的版本判 `Unsupported`，启动 / 放行 / 引擎恢复一律拒绝 | 旧批次记录仍可打开：`SnapshotSchemaTests` 的三份黄金样本（V1 / V2 / V3 口径）必须保持 Valid |
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
| 0 基线与 ADR | **已完成（2026-10-03）** | `docs/adr/0001-runtime-boundaries.md`：单进程单实例、SQLite + WAL、进程内通道调度（意图先落库）、仿真按设备行启用，每条写明理由、代价、"什么时候该重新评估"和守卫它的测试；README 增「架构约束」小节链到它。新人只看 ADR 能答"能否多实例 / 用什么库 / 仿真何时启用"。测试基线（按项目跑）：`dotnet test tests/RecipesManage.{Domain,Contracts,Execution,Api}.Tests`，当前 278 / 7 / 192 / 5 全绿。 |
| 1 仿真 PLC 可关 | **已完成（2026-09-25，做法与计划不同）** | 没有加 `Plc:EnableSimulators` 配置开关，而是按**库里的设备行**判：`MB-01`/`S7-01`/`UA-01` 存在、启用、协议+端口对得上且 host 是回环才绑端口（`Simulation/PlcLoopbackGate.cs`；2026-10-02 起仿真独立成 `RecipesManage.Simulation` 项目）。理由：设备行是这台机器"要连什么"的唯一真源，再加一个配置项就会有两份真源（配置说开、库说没有 → 端口白占）。`Seed:Demo=false` 时那三条行本来就不写，所以生产机零占用；判据只在启动时跑一次。 |
| 2 授权单轨化 | **部分完成（2026-10-02，做法与计划不同）** | 没有按计划"服务层去掉纯角色检查"：电子签名路径需要纵深防御，服务层被别处直接调用时也必须自保。改成**名单单轨**——能力（现 16 项，2026-10-04 增 `batch.record.view` / `audit.view`）的角色名单只在 `Domain/Identity/Capabilities.cs` 写一次；`AuthorizationPolicies` 由它生成，服务层统一 `EnsureCan(Capabilities.X)`（`CapabilityExtensions`），约 30 处手写角色列表清零。这不是洁癖：注入仿真故障的策略放行 Admin 而服务层拒绝 Admin，已经漂移过；现拆成独立能力 `equipment.simulate`。`CapabilitiesTests` 把"角色 × 能力"矩阵与职责分离规则（仅质量可放行、仅主管可跳步、Admin 不碰产线）钉成测试。另补了 `ValidateTagMap`/`TestConnection` 缺失的服务层二次校验。**`docs/auth-matrix.md` 已补（2026-10-03）**：能力×角色、接口×门槛两张表由 `AuthMatrixTests`（新项目 `RecipesManage.Api.Tests`，反射读控制器特性 + `Capabilities`）生成，代码与文档不一致即失败；同时钉住"每个接口必须声明门槛"、匿名接口白名单（登录、`api/health`）、"登录即可的写接口"白名单（仅 `decide`，附理由）。生成时就抓到一处缺口：`POST /api/alarms/{id}/ack` 的能力 `alarm.ack` 只在服务层判，第一道门漏挂策略，已补（行为不变，仍是 403，只是更早拒绝）。**读权限已定（2026-10-04）**：新增能力 `batch.record.view`（批记录 / PDF → 主管 / 质量 / 管理员）与 `audit.view`（全局审计日志 → 质量 / 管理员），控制器策略 + 服务层 `EnsureCan` 两道门，其余 `GET` 维持登录即可。`recipes/{id}/decide` 的动态节点授权有意留在 `RecipeApprovalService`（按审批节点的 `RequiredRole` 判，运行时才定，放不进静态能力表）。 |
| 2b 电子签名结构化 | **已完成（2026-10-02）** | 批次/化验签名从"audit_logs 里一行 含义+备注 的拼接文本"变成表 `signature_records`：**含义原文在签署时冻结**，备注单独成列；批记录读库里的文本，不再用当前代码的含义表反查（以前改一次措辞，历史批记录展示的就不是当时签的那句，不满足 21 CFR 11.50）。迁移 `20261002130000_SignatureRecords` 回填历史 `*.esign` 审计行：已知措辞拆成含义+备注，更早的措辞整段原样留作含义。写入统一经 `EsignGuard.Record`，同时保留审计行。**未做**：签名与被签对象内容哈希的绑定；配方侧签名（审核节点含义已冻在 `approval_records`，提交/升版等仍只在审计日志）。 |
| 3 拆分 Application 服务 | **部分完成（2026-10-02）** | `BatchService`（原 ~930 行）按读写拆开：读路径（列表 / 详情 / 趋势 / 握手履历 / 快照漂移 / 批记录 / PDF / 报警列表）→ `BatchQueryService`，只依赖数据库、当前用户、物料服务、PDF 渲染器；`BatchService` 只剩写路径（创建 / 启动 / 中止 / 保持 / 恢复 / 跳步 / 确认 / 放行 / 拒收 / 报警确认），返回详情时委托查询服务，反向无依赖。`EsignGuard` 改为容器注入（`BatchService` / `RecipeService` / `MaterialLotService` / `ApprovalChainService` 四个服务都不再自己 `new`，`ArchitectureBoundaryTests` 用 Theory 钉住）。跳步规则下沉到领域 `Domain/Batches/StepSkip`：目标解析（`ResolveTarget`）、放行判定矩阵（`Decide`：转发给握手引擎 / 离线直接应用 / 拒绝并带错误码）、离线状态迁移（`ApplyOffline`）都是纯函数，`StepSkipTests` 覆盖每个拒绝码与放行路径；`BatchService.SkipAsync` 只剩装载、落库、审计、发布。`ArchitectureBoundaryTests` 钉住读路径不得依赖调度器 / 租约 / 密码校验。`RecipeService`（原 ~390 行）按职责拆成四个：编辑 `RecipeService`（建 / 改表头 / 保存工艺 / 升版）、读 `RecipeQueryService`（列表 / 详情 / 版本对比，构造函数只有数据库）、审批流 `RecipeApprovalService`（提交 / 审核 / 驳回重开 / 指定审批链，含按节点判定的 `decide` 授权）、导入导出 `RecipePackageService`；装载、映射、审计这几件无状态的事放进内部静态类 `RecipeSupport`，顺带去掉了 `BatchQueryService` 对 `RecipeService` 的依赖。`ArchitectureBoundaryTests` 钉住查询服务只依赖数据库、四个服务互不持有。`recipes/{id}/decide` 的动态节点授权留在 `RecipeApprovalService`（按审批节点的 `RequiredRole` 判断，不属于静态能力表，这是有意的）。**未做**：放行 / 拒收没有单独成类（两个入口 ~45 行，拆出来只会多一份共享依赖）。 |
| 4 调度意图落库 | **大部分完成（2026-10-03 核对；实现早于本行更新）** | 盘点结论：Hold / Skip / Confirm 三类操作员指令已全部落 `scheduler_intents`（迁移 `20260921100000_SchedulerIntents`，2026-10-02 起按工步分行 `SchedulerIntentPerStep`）。`BatchService` 在**入队之前**先把意图随批次状态同一次落库；调度器启动时 `RecoverRunningAsync` 先把库里全部意图装回内存，再拉起 Running / Queued 批次，每次 `StartBatch` 前再 `HydrateIntents` 一次；意图的消费与工步结论**同一次提交**里删除，冲突重载不会让已消费的意图复活（否则重启后会把执行过的跳步 / 确认再执行一遍）。Abort 有意不做成意图：批次先定稿为 `Aborted` 再入队，终态本身就是权威记录；租约由启动时的对账（`EquipmentLeaseService`）清掉失效的。同机重启的单实例约束写在 `docs/deployment.md` §2，由 `SingleInstanceLock` 强制。验收覆盖：运行中重启不重写已完成的升温 / 不重发 PLC 载荷（2 个）、Hold 重放、**Confirm 重放、Skip 重放（2026-10-03 补）**。ADR 已补（`docs/adr/0001-runtime-boundaries.md`，2026-10-03）。**未做 / 未验证**："调度以库为准消费"没有做——命令仍由进程内通道驱动，库是重放日志，这是取舍而非缺口；崩溃落在"批次已定稿 Aborted、调度器还没复位设备"之间时，重启只对账租约、不会补发设备复位（这点仍成立）。**但它的后果已堵住（2026-10-03）**：核对时发现真正的危险不在"设备没复位"，而在下一个批次——会话的第一个工步无条件"从 PLC 当前相位续跑"，设备上残留的 Step_Complete 会让新工步直接进归档、残留 Step_Running 会让它空等别人的时长，两种都会在**一次参数都没写给 PLC** 的情况下把工步记成完成（批记录上还带归档实测）。残留来源不止崩溃：中止时 PLC 连不上、复位被吞掉而租约照放；崩在"工步记完成"与"复位 Step_Complete"之间（每步 100ms 量级的窗口）；现场手动动过设备。两个新测试（`StaleHandshakeTests`）先复现了（一个"没写参数就完成"，一个空等 120s），现改为：续跑只在上位机记得这一步发过指令时成立（进入时工步已是 Running / Held，`MarkStarted` 在写 PLC 之前落库）；进入时工步仍是 Pending 却见到 Running / Complete / Trigger 回显，一律视为残留，先复位再按全新工步握手，并留一条 `reset` 履历和一条警告日志；复位后设备仍不空闲就由状态机的 `PlcNotIdle` 拒绝写入并报故障。**孤儿设备主动复位已补（2026-10-03）**：新表 `pending_device_resets`（迁移 `20261003100000_PendingDeviceResets`，每台设备至多一行）。`BatchService.AbortAsync` 对已启动过的批次（非 Queued）在批次定稿 Aborted 的**同一次提交**里登记欠账；调度器复位**确认成功**（三个握手位都写成功）才删，并留一条 `device.reset` 系统审计。后台 `PendingResetLoopAsync` 启动时清一遍，清不掉（PLC 连不上 / 写失败）按 `PendingResetRetryInterval`（30s）重试，中止后复位失败也会唤醒它；不卡命令循环。设备已被别的批次占用则作废标记、不去写（会打断那个批次的握手）；来源批次的中止收尾还在进行则让它自己做。测试 `PendingDeviceResetTests`（8 个，含设备复位与租约、中止标记写入的互斥回归）；删掉启动循环做过变异验证（3 个调度器用例变红）。**剩余风险已关闭（2026-10-04）**：复位以 `IdleBitsClear`（Running / Complete / Trigger / Held / HostHold 全清）回读确认，失败保留欠账重试；`EquipmentOperationGate` 按设备 ID 升序加锁，统一保护待复位重试、租约申请、中止标记同批次提交、调度器中止复位及释放租约。复位线程拿锁后重查最新标记，避免基于启动快照处理已变化的欠账。此锁保证当前单进程单实例 + SQLite 的安全边界；多进程部署前须替换为跨进程协调。 |
| 4b SQLite 连接参数 | **已完成（2026-10-03，由追偶发失败发现）** | 新连接的 `busy_timeout` 默认是 **0**：别的连接正在写时立刻报 `database is locked`，不等待；`Default Timeout=180` 试过，完全没用（那只管语句级重试，管不到提交阶段）。引擎测试在 12 路并行压力下约 6% 的次数把一个所有工步已完成的批次置成 `Faulted(ENGINE)`——即**生产里同样会发生**：调度车道、HTTP、备份、维护各自开连接写同一个文件。现由 `SqliteConnectionPragmas`（连接拦截器）在每条连接打开时设 `busy_timeout=10000`，挂在唯一的生产配置入口 `RecipesDatabase.Apply` 上；引擎测试宿主改走同一入口，不再自己 `UseSqlite`。修后同样压力 96 轮 × 3 个用例 0 失败（修前约 6%）。同时 `ENGINE` 故障消息带上最内层原因（`DescribeFault`）：此前只有 EF 的外壳 "An error occurred while saving the entity changes"，现场根本看不出是锁。**另：WAL 模式补上（同日）**——此前代码里没有任何地方设 `journal_mode=WAL`，而 `DatabaseBackup` / `RecipesDatabase` 的注释都写"库跑在 WAL 模式下"：EF 只在**它自己创建库文件**时才会切 WAL，而 `SchemaBootstrap` 先探表（探表本身就建出了空文件），所以新装机的库实测是 `delete`（回滚日志）模式，只有历史遗留的开发库是 WAL。现由 `SqliteJournal.EnsureWalAsync` 在 `SchemaBootstrap.ApplyAsync` 迁移**之后**切换（升级快照与迁移仍在原模式下进行；模式写进库文件，只需一次，旧装机自动转换）；`synchronous` 保持默认 `FULL`，不降到 `NORMAL`（电子签名/审计数据，宁可慢也不能在掉电时丢最后几次提交）。配套：VACUUM 之后立即 `wal_checkpoint(TRUNCATE)`，避免 `-wal` 涨到与库同大；备份产物仍是单个自包含文件（`WalModeTests` 覆盖：新库为 WAL、旧回滚模式库转换不丢数据、WAL 库的备份无 `-wal`/`-shm` 且含最新提交、VACUUM 后 `-wal` 为 0 字节——后者做过变异验证）；`docs/deployment.md` §2.1 写明三个文件的含义、禁止网络盘/同步盘、手动拷库的正确做法，§8 恢复演练补上"同时移走旧的 `-wal`/`-shm`"。实机起 Api 验证：新库启动后 `recipes.db-wal` / `-shm` 出现；WAL 下同样压力 96 轮 × 3 个用例 0 失败。**恢复已脚本化（2026-10-04）**：`deploy/restore-backup.ps1` 把 §8 手工流程变成代码——校验快照（SQLite 头 + 换入后 SHA256 比对）、停服务并先禁看门狗、旧库连同 `-wal`/`-shm` 隔离到 `App_Data\pre-restore-<utc>\`、换入、拉起探活，探活失败自动回滚旧库；`-WhatIf` 干跑、`-NoStart` 只换文件（演练）。沙箱三场景已验证（换库哈希一致/坏快照拒绝且退出码 2/WhatIf 零改动）；§8 手工步骤保留为最后手段。 |
| 5 巩固项 | **已完成（2026-10-04 复核）** | JWT/密钥：非 Dev 缺/弱占位密钥直接拒绝启动（实测）。前端实时：`executionHub.ts` 显式管理订阅生命周期（页面卸载退订批次组、重连后整表重订阅、断流状态可见）。Waves 可读性：已拆成 Lane / HostSteps / Gates / ReadTolerance / LaneSupport。授权矩阵：`AuthMatrixTests` 生成并守护 `docs/auth-matrix.md`。 |

### 电子签名内容绑定增量（2026-10-04）

阶段 2b 原列“签名与被签对象内容哈希绑定”先完成化验样品判定切片：迁移 `20261004053413_SignatureContentHashBinding` 为签名记录增加可空 `ContentHashVersion` / `ContentHash`；新判定在样品状态更新后，以版本 1 的规范字段顺序计算 SHA-256，并与样品判定、结构化签名及审计日志同一次提交。批记录页面与 PDF 显示签署人、时间、含义、摘要和校验状态；历史签名不回填，明确标为“历史签名无摘要”。`MaterialGenealogyIntegrationTests` 覆盖正常匹配与结果篡改后不匹配，迁移测试覆盖旧记录空摘要，PDF 测试覆盖签名区渲染。

2026-10-04 继续完成批次质量放行 / 拒收绑定：批记录 API 为签署证据生成版本 1 SHA-256，覆盖冻结配方快照（含工步、参数、依赖边及设备映射）、工步质检、握手履历、全量过程样本、配方审批、报警、PLC 写参计划、物料使用和实验室样品；对快照、映射及证据集合做稳定排序。排除当前配方漂移、生成时间、批次处置元数据及签名列表，避免实时数据和签名本身造成摘要变化。前端随放行 / 拒收提交页面所见版本与摘要；服务端在 SQLite 事务内重新读取、计算并比较，不一致返回 `409 EVIDENCE_STALE`，匹配后与处置、签名及审计同次提交。历史签名显示 `Unbound`，新签名显示 `Verified`，证据变化显示 `Mismatch`，未知版本显示 `Unsupported`；批记录页面和 PDF 均展示摘要及校验状态。`MaterialGenealogyIntegrationTests` 覆盖过期摘要拒绝、成功放行 / 拒收、旧签名兼容及样品证据篡改后失配。

同日补齐执行期批次签名：启动 / 重试 / 中止 / 保持 / 恢复 / 跳步 / 确认的 SHA-256 绑定动作码、签署含义、签署人、原因或目标，以及稳定排序后的冻结配方快照。这里刻意不纳入批次状态、过程样本、握手履历等持续变化的运行态数据，避免正常生产进展让旧操作签名变成误报。批记录读取时重算并验证；旧签名仍标为 `Unbound`。`OccupancyAndImportTests` 覆盖新签名有效、旧签名兼容、运行数据增长不影响校验及签名原因被篡改后失配。

随后增加 SQLite `signature_records` 更新 / 删除触发器：应用数据库拒绝改写或移除已落库签名，同时允许追加新签名；迁移回归以原始 SQL 验证拒绝行为。此为纵深防护，不是管理员级不可篡改机制。

**仍未完成**：数据库所有者可直接移除触发器，也可同时改业务数据、摘要和触发器，因此仍需要独立信任域的外部信任锚（例如带签名的定期审计根哈希导出到只追加存储或独立服务），才能覆盖 DBA 威胁模型。

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

**没做，且需要单独一轮**：SQLite 写侧合批（多设备订单真来了再说）；多站点/租户（建议用
"每站点一套独立部署"绕开，见 [[brmes-single-machine-gaps]] 的判据）。

---

## 2026-10-04 代码评审改进

先堵"现场装完打不开界面"，再定读权限口径，然后还性能债。

| 做 | 落点 |
| --- | --- |
| 前端随包同源托管 | `frontend/vite.config.ts` 的 `build.outDir` 指向 `src/RecipesManage.Api/wwwroot`；`Program.cs` 挂 `UseDefaultFiles` / `UseStaticFiles` 与 SPA 回退（`/api`、`/hubs` 未知路径仍 404 JSON，带扩展名的请求不回 index.html，未构建时给可照做的提示）；`publish-package.ps1` / `install-watchdog.ps1` 出包前先 `npm ci && npm run build`（含 vue-tsc 类型检查）；`deployment.md` 写明装完直接开 `http://localhost:5010/` |
| 读权限收紧 | 批记录 / PDF → 主管 / 质量 / 管理员（`batch.record.view`）；全局审计日志 → 质量 / 管理员（`audit.view`）。控制器策略 + `AuditService.QueryAsync` / `BatchQueryService.RecordAsync` 服务层自保；前端路由 meta 与监控页按钮同步收敛；`auth-matrix.md` 重新生成 |
| 取样入口不下线 | 批记录不再对车间开放后，取样移到监控页（`lot.handle` 角色）；`CreateSampleAsync` 未指定物料批时默认绑本批产出批，谱系链接不断 |
| 物料谱系不再整表加载 | `MaterialLotService.GenealogyAsync`：祖先沿 `ParentLotId` 逐层取、后代每层一条 IN 查询；顺序语义仍由 `MaterialGenealogy` 决定 |
| 投料绑定去 N+1 | `BindSnapshotLotsAsync` 一次预取投料批与既有绑定，2N 次往返降为 2 次 |
| 复位回读确认 | `TryIdlePlcAsync` 写完后回读握手位（`IdleBitsClear`），3 次短重试；未确认即返回 false，欠账保留重试 |
| 轮询并发护栏 | `usePolling` 内置 in-flight 跳过，慢请求不再叠罗汉（页面级合并窗口之外的兜底） |
| e2e 同步与基建 | `brmes-loop` / `genealogy` / `ui-shell` 按新契约更新断言（含"操作员看不到批记录入口"与越权重定向）；修掉 `helpers.loginAs` 的 `isVisible()` 竞态（顶栏未渲染时直接 goto /login 会被守卫弹回 /dashboard） |
| 一键验证 | 新增 `verify.ps1`：后端四个测试项目 + 前端 vitest + `vue-tsc && vite build`；`-WithE2e` 追加冒烟（SPA 由 API 托管、未知 /api 仍 404）与 Playwright 全套（`-E2eSpec` 可只跑单个 spec）；API 已健康则复用、只有自启的才停 |
| 恢复程序化 | 新增 `deploy/restore-backup.ps1`（见阶段 4b 行）；`deployment.md` §8 改指脚本，示例统一为 `powershell -File`（本机只有 Windows PowerShell 5.1，`pwsh` 示例照抄跑不起来） |
| 编译警告清零 | `EquipmentService.DashboardAsync` 的批次号子查询补空串兜底（CS8604），全解决方案 0 警告 |

验证：Domain 298 / Api 5 / Execution 202 / Contracts 7 全绿；前端 vitest 67 全绿 + `vue-tsc && vite build` 通过；实机冒烟（`/` 返回 index.html、深路由 200、`/api/nope` 404 JSON、favicon 404、asset 200）；e2e：brmes-loop、genealogy、ui-shell 相关用例全过。**另（同日）**：`verify.ps1` 两种模式实跑通过（默认；`-WithE2e -E2eSpec genealogy.spec.ts` 含自启 API、冒烟、e2e、自清理）；`restore-backup.ps1` 沙箱三场景通过（换库/坏快照拒绝/WhatIf 零改动）；全解决方案 0 警告。

---

## 2026-10-04 UI 易用性走查改进

做法：把 Api 起在 5010，用 operator / admin 两个角色只读走查主路径（开批 → 监控 → 报警 → 放行，以及配方 / 审批链 / 设备 / 用户 / 审计），再对照代码核实每条发现后落地。

| 做 | 落点 |
| --- | --- |
| 操作列不再被横滚藏起来 | 报警页「确认」列与设备页「操作」列钉 `fixed="right"`；设备页操作列 360 → 220（实测按钮宽度），1366 笔记本上此前要横向滚动才能操作设备 |
| 审计关键词改全库检索 | `AuditService.QueryAsync` 增 `q`（用户名 / 动作码 / 实体 / 详情 LIKE）与 `qTokens`；中文动作 / 实体标签库里不存在，前端 `labels.ts auditSearchTokens` 反查 code 随参数带给服务端 OR；关键词搜索不再是"只过滤当页 50 条"（"没搜到"曾会被读成"没发生过"）。LIKE 口径提为 `SearchLike`（批次 / 报警 / 审计共用，原 `BatchQueryService.NormalizeLike` 私有副本删除） |
| 审计测试加钉 | `AuditQueryTests`：跨页命中、`%`/`_` 转义、中文标签经 qTokens 命中、token 上限 50 |
| 审批链编辑有"反悔"入口 | 草稿指纹 + `dirty`：切链 / 点「新建链」前先确认、有改动才出现「放弃修改」；顺带修掉右栏空态判断（原 `v-if="!draft"` 恒假，空态从未显示过） |
| 侧栏未确认报警徽标 | 新 `stores/alarms.ts`：`take=1` 轻查询，shell 挂载 + 30s + 路由切换刷新，报警页 / 监控页确认成功后立即 refresh；不订 dashboard 组（避免全站事件灌进共享连接） |
| 配方只读黄条指向「升版」 | 生效版本且无草稿时，只读提示补一句"点右上「升版」基于本版创建可编辑草稿"，仅对工艺工程师显示 |
| 用户管理防打错 | 新建 / 改密加「确认密码」（与新密码一致校验）；停用开关旁注明"停用后无法登录，可随时重新启用" |
| 监控页握手位可收敛 | `LaneSignalsCard` 加「只显示非 0 位」开关（默认仍全量，正常运行时一排 0 位不再占视线） |
| 空态与标签解释 | 批次列表「待检终样」标签挂 HelpTip（复用 glossary）；设备 / 批次空态补新装机顺序引导 |

验证：前端 vitest 67 全绿 + `vue-tsc && vite build` 通过；`AuditQueryTests` 全绿；`verify.ps1` 默认模式实跑通过（Domain 298 / Contracts 7 / Api 5 / Execution 204 + 前端 67）；API 级实测审计检索（「生产批次」仅 LIKE 0 条、带 qTokens 51 条；字面 `%` 0 条）；真机复验 6 项 UI 改动（徽标 3、审计 50/51、审批链放弃流程、设备操作列免横滚、用户改密二次输入、配方升版指引）。e2e：ui-shell 28 例全过——顺带修掉「斜杠聚焦」用例的竞态（`goto` 后不等搜索框渲染就按 `/`，会被判成"本页没有搜索框"而静默跳过，单跑必失败、整包靠重试侥幸过；改为先 `waitFor` 再按）。

**未做 / 待确认**：取样按钮按批次状态收敛（未启动批次也能取"终样"，属产品口径问题，未动行为）；390px 手机窄屏实测（本次走查受浏览器视口限制，未验证）。

---

## 2026-10-04 UI 易用性走查（第二轮）

同一套做法（实机只读走查 + 代码核实）又过了一遍设计器、相库、配方列表与空态。

| 做 | 落点 |
| --- | --- |
| 相库表两个同名列拆开 | 相库表格里数据列与动作列都叫「操作」，同一张表两个同名列分不出哪个是数据哪个是按钮。数据列（ISA-88 的 Operation）改名「工艺操作」并挂 `Operation` 术语提示；配方设计器与相模板对话框的同一字段标签一并统一，避免同一字段两种叫法 |
| 删除工步加确认 | 配方设计器「删除工步」原本点一下就删：连同该工步全部参数槽（设定值/上下限/单位）与所有连线，且没有撤销。现弹确认并读出要丢什么——「将删除工步「S10 升温至固溶温度」及其 参数 2 槽、连线 1 条，此操作不可撤销。」（只说"确认删除？"看不出与删一条连线的轻重差别） |
| 新建配方表单重置 | 「新建主配方」对话框用的是模块级 reactive，取消后再打开仍留着上次输的编码/名称，容易顺手提交旧值。打开时清空 |
| 空态引导不再误报 | 上一轮加的新装机引导（设备 / 批次空态）在取数失败时也会出现——那时"还没有设备/批次"是假信息，用户该去看错误条。补 `!error` 守卫 |
| 升级后界面不生效的根因 | 前端随包同源托管后静态文件没有任何 `Cache-Control`：浏览器按 `Last-Modified` 推断"启发式新鲜期"，把 `index.html` 缓存住 → 升级后仍拿旧入口、去请求已删除的旧 `/assets/xxx.js`（404），现场只表现为"升级没生效、要强刷"（本轮走查就复现了：相库表头初次进页面还是旧的）。改为按文件分类：`/assets/*` 内容哈希命名 → `max-age=31536000,immutable`；`index.html`（含 SPA 回退那条路径）→ `no-cache`，每次回源确认（允许 304，不是禁用缓存） |

验证：前端 vitest 67 全绿 + `vue-tsc && vite build` 通过；e2e：phase-library 3 + ui-shell 28 + brmes-loop / recipe-package 3，全过（`--retries=0`）；后端 Api 5 / Contracts 7 全绿；实机复验 4 项（相库列头为「工艺操作 / 操作」、新建配方再打开编码为空、删除工步确认文案含工步名与参数/连线数且取消后工步数不变、有数据时无空态误报）；缓存头实测 `/` 与深路由 `no-cache`、`/assets/*.js` `immutable`，`/api/nope` 仍 404 JSON、favicon 404 不变。

---

## 2026-10-04 UI 易用性走查（第三轮）

这轮盯的是「默认空表格」——Element Plus 表格没数据时统一显示「暂无数据」，读起来像页面坏了或归档漏了，而多数情况下那恰恰是正常结果。

| 做 | 落点 |
| --- | --- |
| 趋势图空态 | 批次刚创建 / 未进入工步时，「趋势」页签原本是一个 260px 高的空白块，分不清"没开始采样"还是"图坏了"。改为显示一句话，并 `v-show` 藏掉两个画布（保留 DOM，echarts/uPlot 绑定不被拆掉）。**这里的空态判断必须是函数不能是 computed**：`series` 是刻意做成非响应式的普通对象（高频采样不走 Vue 依赖追踪），computed 会首帧把 false 永久缓存住 |
| 批记录握手区块空态 | 未启动的批次在「四步握手时序」区块原本显示一张空表（「暂无数据」），改为「本批次尚无握手事件（未启动，或未产生过合法动作）。」 |
| 版本差异弹窗去空表 | 只有"整步增删"而字段没变时，弹窗上方已写明「新增/删除工步」，下面却还摊一张「暂无数据」的空表，两句话互相打架。改为只在有字段级差异时渲染表格（审核台早已是这么做的，这里对齐） |

**试过又撤回的一处**：给「快照 vs 当前生效主配方」区块也加空态说明，实机复验时发现该接口**总是**返回行（无漂移时也是 `drifted=false` 的逐项对照），空态路径在这套数据模型下不可达，属死代码，已撤回（含对应的 en 词条，避免 i18n 死键）。

验证：前端 vitest 67 全绿 + `vue-tsc && vite build` 通过；e2e：ui-shell 28 + execution-closed-loop 2 + phase-library 3 + brmes-loop 1 + recipe-package 2，全过（`--retries=0`）；实机复验：未启动批次趋势页显示「本批次还没有过程样本（启动执行并进入工步后才开始采样）。」且无空图表框、已跑批次显示「共 12 条样本，全部绘出。」、批记录握手区块显示空态说明。**过程中自查出一次自己的回归**：`computed → function` 改写时误删 `chartKind` 声明，导致监控页整页空白，e2e 立刻抓到，已修复并复跑通过。

---

## 2026-10-04 UI 易用性走查（第四轮）

这轮三件事：把"校验时机"推到填的时候就地说、给配置台补离开守卫、收口硬编码中文。

| 做 | 落点 |
| --- | --- |
| 设备表单就地必填校验 | `EquipmentFormDialog` 的「编码 / 名称」原本没有校验——后端这两项也**没有**校验（空编码会一路写库，是这次顺带发现的后端口径缺口，本轮只堵 UI 侧）。加了必填规则；校验失败时把页签切回「基本与超时」，否则红字藏在未显示的页签里，用户看着像"点了保存没反应"。该后端校验缺口已在后续独立跟进中关闭，见「设备编码与名称后端必填校验」 |
| 相模板编码必填校验 | `PhaseTemplateDialog` 编码对齐后端 `PHASE_CODE` 就地校验；名称可空（后端用类型名兜底），写进占位符「留空用类型名」说清楚 |
| 审批链配置离开守卫 | 右栏改到一半点侧栏换页 / 刷新 / 关标签都会被静默丢弃。补 `onBeforeRouteLeave` + `beforeunload`，与配方设计器同一套写法 |
| 硬编码中文收口 | 24 条 toast / 确认框文案 + 12 条加载失败提示与空态原文（`批次详情加载失败：${error}` 这类模板串）此前都没走 `t()`，英文界面会露中文，且 i18n 覆盖率测试**扫不到**（它只扫 `t()` 调用点，硬编码在它视野之外）——这轮是手工 grep 各提示入口扫出来的，改走 `t()` 并补 en 译文后覆盖率棘轮回到 0 |

验证：前端 vitest 67 全绿（含 i18n 覆盖率棘轮）+ `vue-tsc && vite build` 通过；后端 Api 5 / Contracts 7 全绿。**全量 Playwright 暴露的 7 处失败，逐条核对后全部与本轮改动无关**——是产品早就本地化了界面文案、而部分 spec 还断言英文枚举/旧状态码的"陈旧断言"，以及两次连跑下的负载抖动：

| 处置 | 说明 |
| --- | --- |
| `skip-alarm` 修复两处 | ① 报警表在监控页「报警」页签里，spec 没切页签就断言卡片可见；② `getByRole("button", { name: "确认" })` 的 name 默认是**子串匹配**，把页头的「确认全部 N 条」也算进去，触发 strict mode 违规——加 `exact: true`。改后 2 例全过 |
| `version-hold-occupancy` 修三处陈旧断言 | ① 仪表盘协议列显示 `labels.ts` 的中文标签（Simulator → 仿真器），spec 还在断言 `Simulator`；② 设备占用是"重试可能成功"的冲突，产品映射为 **423 Locked**（`ExceptionHandlingMiddleware` 有意为之），spec 还断言 400；③ 状态标签断言 `· Created ·` → `· 已创建 ·`。改后 1 例过（1.5 分钟） |
| `process-ops-hold` 修两处陈旧断言 | 页面标题里的相位/状态早已本地化：`StepRunning` → `工步执行中`、`· Aborted ·` → `· 已中止 ·`。改后 2 例全过 |
| `brmes-loop` / `ui-shell（中途断供）` | 单跑均通过（33.9s / 19.2s），确认是全量 29 分钟连跑下的负载抖动，非回归 |
| `parallel-units` **仍未过（待单独排查）** | 失败点：建批后断言标题含 `UP-固溶→HT-01`。查了这条批次的 API 响应：`snapshot.unitEquipment` 为**空**，即经由创建对话框选择的单元绑定没有落进快照。UI 侧尚未定位到是对话框状态被轮询重置还是创建侧的问题，需要单独一轮（现象与证据已留档） |

---

## 2026-10-04 多单元绑定 E2E 复核（第五轮）

单跑 `parallel-units.spec.ts` 后发现，原失败并非批次 API 丢失绑定：并行单元弹窗默认继承配方设计器当前的 `FURNACE` 设备类，而用例随后要求将该单元绑定到 `HT-02`（`QUENCH`）。页面按设备类和 PLC 程序兼容规则禁用了 `HT-02`，旧 E2E helper 在未匹配到指定设备时静默回退到任意空闲设备，最终所有单元落到主设备；服务端按约定将相同设备映射规范化为 `null`。因此此前“快照为空”的观测是测试配置与 helper 回退共同造成的假故障，不是创建服务持久化缺陷。

| 修复 | 落点 |
| --- | --- |
| 指定并行单元设备类 | `parallel-units.spec.ts` 明确为 `UP-淬火` 选择 `QUENCH`，使 `HT-02` 成为兼容设备 |
| 禁止 E2E 静默换设备 | `createBatchFromApproved` 对指定设备要求唯一匹配且可选；不可用时直接失败，不再挑选其他空闲设备 |
| 覆盖请求到快照 | 用例断言 POST 请求中淬火单元使用非主设备，并断言创建响应快照保留同一设备 ID |

验证：`parallel-units.spec.ts --retries=0` 通过，覆盖设计、审核、双设备批次创建和两台 PLC 的并行执行；批次创建请求与 API 返回快照中的单元绑定一致。

---

## 2026-10-04 设备编码与名称后端必填校验

关闭第四轮 UI 走查中记录的后端校验缺口。`EquipmentService.UpsertAsync` 在点表解析和数据库读写前拒绝 null、空串与纯空白编码/名称；`EquipmentLine` 构造和更新也守住同一领域不变量。使用 `EQ_CODE_REQUIRED` / `EQ_NAME_REQUIRED` 错误码，默认返回 HTTP 400，并在英文界面提供对应译文。更新请求仍不允许修改设备编码；本次只验证其必填，不改变既有不可变语义。

| 验证 | 结果 |
| --- | --- |
| `EquipmentLineTests` | 10 项通过，覆盖创建/更新拒绝空值、空串和空白，以及有效编码规范化 |
| `EquipmentUpsertValidationTests` | 9 项通过，覆盖服务创建/更新校验优先于点表解析和数据库查找 |
| `BackendMessageCatalogTests` | 4 项通过，静态错误文案均有英文译文 |
| 后端完整测试组 | Domain 308、Execution 214、Contracts 7 全通过 |
| 构建 | 前端 `vue-tsc && vite build` 通过；API 构建 0 警告、0 错误 |

---

## 2026-10-04 待复位与批次租约互斥

关闭阶段 4 中“检查租约后、PLC 复位写入前可能有新批次取得设备”及“中止更新新欠账被旧复位流程清除”的竞态。新增 `EquipmentOperationGate`，按设备 ID 提供进程内异步互斥；批次申请或中止绑定多台设备时按 ID 排序加锁，避免死锁。待复位流程在读取租约前取得锁并重查最新标记，持有到 PLC 复位回读确认及欠账处理完成；中止标记写入与批次定稿同锁、同次提交；调度器中止复位直到租约释放也持有同一组锁。因项目当前明确为单实例调度器 + SQLite，此协调覆盖生产租约、复位与标记变更；横向扩展前必须改为共享/持久化协调机制。

新增确定性并发测试：用可阻塞 PLC 驱动停在复位写入期间，同时申请同设备的新租约或中止另一批次续写复位欠账；断言两种操作都等待复位完成，且新欠账不会被旧复位流程误删。`PendingDeviceResetTests` 8 项通过；Execution 全量 215 项通过。API 构建 0 警告 / 0 错误；重启后 PID `13848`，`/health` 与 `/login` 均返回 200。

---

## 修订记录

| 日期 | 说明 |
| --- | --- |
| 2026-09-22 | 初稿：基于当前 slnx 分层、DI、调度器与授权现状整理 |
| 2026-09-30 | 阶段 1 完成（做法改为按设备行判定，见上表）；补「面向更多客户」一节记录 A 档六项与其未做部分；`origin` 已是同盘镜像而非"无远程" |
| 2026-10-03 | 核对阶段 4：Hold / Skip / Confirm 意图落库与重启恢复早已实现，补 Confirm / Skip 重放测试；追引擎测试偶发失败，定位为连接 `busy_timeout=0`（新增阶段 4b），并发现代码从未设置 WAL |
| 2026-10-03 | 强制 SQLite WAL 模式（`SqliteJournal`，迁移后切换；`synchronous` 保持 FULL）；VACUUM 后截断检查点；`deployment.md` 补 §2.1 与恢复演练的 sidecar 清理；新增 `WalModeTests` |
| 2026-10-03 | 堵住"设备残留握手位把新工步带着记完成"：工步进入时仍是 Pending 却见残留 Running / Complete / Trigger 回显，先复位再按全新工步握手；新增 `StaleHandshakeTests`（先红后绿） |
| 2026-10-03 | 补 `docs/auth-matrix.md`（从代码生成并由 `AuthMatrixTests` 守护，新项目 `RecipesManage.Api.Tests`）；补 `alarms/{id}/ack` 缺失的第一道门策略 |
| 2026-10-03 | 启动时主动复位孤儿设备：新表 `pending_device_resets`，中止同提交登记、复位确认才删、后台补做并重试；新增 `PendingDeviceResetTests` |
| 2026-10-03 | 阶段 0 完成：新增 `docs/adr/0001-runtime-boundaries.md`，README 增「架构约束」小节 |
| 2026-10-03 | 工步进行中读 PLC 失败的容忍：握手轮询 / 写参回读 / 归档实测的读失败，窗口内（默认 30s，自上次读成功起算，`readFailureToleranceSeconds` / `readRetrySeconds` 可按设备配）只重连重读、不推进状态机、不写任何信号；用尽报 `PlcCommLost`；恢复后心跳基准顺延（`NoteReadGap`），其余看门狗仍走墙钟。**写 / 触发 / 复位失败不在容忍内**。配置类错误（非线路异常）不等待。新增 `ReadGapTests`、`ReadFailureToleranceTests`（断线期间对 PLC 写入次数为 0，已做变异验证） |
| 2026-10-03 | 快照 `schemaVersion`：新批次写 1 并用 V3 哈希；旧批次无字段按 0 读、哈希口径不变；新增 `Unsupported` 状态（前端有对应标签）；新增 `SnapshotSchemaTests`（含三份黄金样本；已做变异验证：哈希去掉版本号则降级测试变红） |
| 2026-10-04 | 代码评审改进落地（见上节）：前端随包同源托管（wwwroot + SPA 回退 + 出包脚本构建前端）；读权限定案（`batch.record.view` / `audit.view`，取样入口移监控页）；谱系逐层查询；投料绑定去 N+1；复位回读确认；`usePolling` 并发护栏；e2e 同步并修 `loginAs` 竞态 |
| 2026-10-04 | 一键验证脚本 `verify.ps1`（默认与 `-WithE2e` 两种模式实跑通过，含自启 API、冒烟、e2e、自清理）；恢复演练脚本化 `deploy/restore-backup.ps1`（沙箱三场景验证）并改写 `deployment.md` §8；文档示例统一 `powershell -File`；清零全仓唯一编译警告（CS8604） |
| 2026-10-04 | UI 易用性走查（operator / admin 实机只读）落地，见上节：操作列钉右侧；审计关键词改全库检索（中文标签经 qTokens 反查 code，`SearchLike` 三处共用）；审批链编辑加「放弃修改」与脏数据确认；侧栏报警徽标；配方只读页指向「升版」；用户改密二次输入；握手位可只看非 0；空态装机引导。同轮修掉 ui-shell「斜杠聚焦」e2e 竞态（先 waitFor 搜索框再按键） |
| 2026-10-04 | UI 易用性走查第二轮：相库表两个同名列拆开（数据列 → 工艺操作）；配方设计器「删除工步」加确认（读出会丢的参数槽与连线数）；新建配方对话框打开时重置；空态装机引导补 `!error` 守卫；并修掉"升级后界面不生效"的根因——静态文件补 `Cache-Control` 分类（`/assets/*` immutable、`index.html` 与 SPA 回退 no-cache） |
| 2026-10-04 | UI 易用性走查第三轮：趋势图空态（无样本时给说明而不是空白块；该判断必须用函数，`series` 非响应式）；批记录握手区块空态；版本差异弹窗不再渲染与上方文案矛盾的空表。漂移区块的空态因数据模型不可达而撤回 |
| 2026-10-04 | UI 易用性走查第四轮：设备/相模板对话框补就地必填校验（设备编码/名称后端无校验，空编码会写库）；审批链配置补离开守卫（路由 + beforeunload）；36 条硬编码中文（toast / 确认框 / 加载失败提示 / 空态）改走 `t()` 并补 en 译文——这类字符串在 i18n 覆盖率测试视野之外，是手工 grep 扫出来的。另修 5 处 e2e 陈旧断言（skip-alarm 页签与 exact 匹配、version-hold 仿真器/423/已创建、process-ops-hold 中文相位与状态）；`parallel-units` 的单元绑定未落快照问题留待单独排查 |
| 2026-10-04 | 化验样品判定签名绑定版本化 SHA-256 内容摘要；批记录和 PDF 展示摘要校验，旧签名标为未绑定；验证结果篡改告警与历史迁移兼容 |
