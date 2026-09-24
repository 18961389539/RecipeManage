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
- 本仓库当前无 git 远程：合并策略以本地分支 / 备份为准；若日后加远程，再按 PR 流程执行。

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
| 0 基线与 ADR | 待开始 | |
| 1 仿真 PLC 可关 | 待开始 | 建议下一个动手项 |
| 2 授权单轨化 | 待开始 | |
| 3 拆分 Application 服务 | 待开始 | 可多 PR |
| 4 调度意图落库 | 待开始 | |
| 5 巩固项 | 待开始 | 可穿插 |

---

## 修订记录

| 日期 | 说明 |
| --- | --- |
| 2026-09-22 | 初稿：基于当前 slnx 分层、DI、调度器与授权现状整理 |
