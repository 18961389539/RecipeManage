# ADR 0001：运行时边界——单进程、SQLite、进程内调度、仿真默认关

- 状态：已采纳（2026-10-03 把已经在代码里执行的约束收拢成文）
- 适用范围：整个后端；前端不受影响

## 背景

这套系统驱动的是**一台现场设备**，并且要留下可审计的批记录与电子签名。这决定了几件事的取舍：
正确性与可恢复性比吞吐和水平扩展重要得多，而且"两个进程同时驱动同一台 PLC"的后果不是报错，是静默写坏。
这些约束原先散落在 README、`docs/deployment.md` 和各类的注释里；新人要回答"能不能开两个实例"得翻三处。本文把它们放在一起，并写明**为什么**和**什么时候该重新评估**。

## 决定

### 1. 单进程，单实例（由代码强制）

- 一份数据库同时只允许一个应用进程。`SingleInstanceLock` 在 `<db>.lock` 上持有独占句柄，抢不到就拒绝启动，退出码 **75**。
- 抢锁发生在建库、迁移、调度器恢复**之前**。
- 判据是独占句柄而不是锁文件存不存在：进程被硬杀时系统自动释放，不会出现"上次崩了，再也起不来"。
- **不支持**同一数据库上的多实例，也不支持"一主一备热切换"。要做备机，是冷备：停主、恢复备份、起备。
- 细节与运维步骤：`docs/deployment.md` §2。

### 2. 数据库是 SQLite，WAL 模式

- 唯一的生产数据库是 SQLite 单文件（`RecipesDatabase` 是唯一的配置入口）。PostgreSQL 支持已移除，不要在个别服务里再引入第二种。
- `SchemaBootstrap` 在迁移之后把库切到 **WAL**；每条连接设 `busy_timeout=10000`（`SqliteConnectionPragmas`）；
  `synchronous` 保持默认 `FULL`——电子签名与审计数据宁可慢，也不能在掉电时丢最后几次提交。
- 单写者是前提，不是缺陷：调度车道、HTTP、备份、维护各自开连接写同一个文件，靠 `busy_timeout` 排队、靠 `ConcurrencyStamp` 乐观并发兜底冲突
  （冲突对操作员表现为 409「请刷新后重试」）。
- 备份走 SQLite Backup API，产物是自包含的单个文件；**不要**直接拷运行中的 `.db`（WAL 下得到的是撕裂的库）。库不能放网络盘。
- 细节：`docs/deployment.md` §2.1、§8。

### 3. 调度在进程内，用内存 `Channel` + 单读者；操作员指令先落库再入队

- 调度器是 `BackgroundService`，命令走 `Channel<SchedulerCommand>`（`SingleReader = true`）。**没有**外部消息队列，也没有跨进程的调度协调。
- 因此**不支持水平扩展**：加实例不会提速，只会让两个调度器抢同一台设备（见决定 1）。
- 内存通道不是权威状态。Hold / Skip / Confirm 在入队**之前**先写进 `scheduler_intents`，随批次状态同一次落库；
  重启时 `RecoverRunningAsync` 先把意图装回内存，再拉起 Running / Queued 批次。意图的消费与工步结论在**同一次提交**里删除。
  所以库是重放日志，命令仍由进程内通道驱动。
- Abort 有意不做成意图：批次先定稿为 `Aborted` 再入队，终态本身就是权威记录；设备租约由启动时的对账清理。
- 本阶段承诺的只有：**同一台机器上进程重启可恢复**。不承诺跨机器接管。

### 4. 仿真 PLC 默认关；是否启用由库里的设备行决定

- 仿真从站（Modbus / S7 / OPC UA 回环）在独立项目 `RecipesManage.Simulation` 中：`Execution` 与 `Infrastructure` 都不认识它，它自己只认契约，只有组合根 `Api` 把它装进来；由 `ArchitectureBoundaryTests` 钉住。
- 没有"开仿真"的配置开关。`PlcLoopbackGate` 看库里的设备行：`MB-01` / `S7-01` / `UA-01` 存在、启用、协议与端口对得上、host 是回环地址，才绑定端口；
  判据只在启动时跑一次。理由：设备行是"这台机器要连什么"的唯一真源，再加一个配置项就有两份真源（配置说开、库里没有 → 端口白占）。
- `Seed:Demo` 在开发环境默认 `true`，其他环境默认 `false`；`false` 时不写仿真设备行，所以生产机零占用。

## 后果

- 好的一面：失效模式少且可预期——要么只有一个进程在写，要么根本没起来；备份、恢复、排障都是"一个文件 + 一个进程"。
- 代价：没有高可用。进程挂了到看门狗重启之间（默认每分钟探活）产线没有上位机监督，靠 PLC 自身的安全逻辑兜底。
- 代价：数据库与应用同机，机器坏了就靠备份；备份必须真的演练过（`docs/deployment.md` §8）。
- 代价：HTTP 写与调度写争同一个写锁；批次数量和采样频率上去后，`busy_timeout` 里排队会变成可感知的延迟。

## 什么时候该重新评估

出现下列任何一条，就不是改个配置的事，要重开这份 ADR：

- 需要**两台以上设备由两个独立进程**驱动（现在是一个进程驱动多台设备，没问题；问题是"一台设备两个驱动者"）。
- 需要在多台机器上看同一份实时数据并**写入**（只读副本不在此列，那可以走备份流）。
- 采样写入使 `busy_timeout` 排队成为常态（可观测信号：`ENGINE` 故障消息里出现 `database is locked`）。
- 合规要求**应用与数据分离部署**或数据库级的访问审计。

## 守卫这份决定的东西

| 约束 | 守卫 |
| --- | --- |
| 单实例 | `SingleInstanceLock`；`docs/deployment.md` §2 |
| 分层与不得反向依赖（Domain ← Application ← Infrastructure；Execution 只依赖 Application；仿真独立） | `ArchitectureBoundaryTests` |
| SQLite 唯一配置入口、`busy_timeout`、WAL | `SqliteConnectionPragmasTests`、`WalModeTests` |
| 意图落库与重放 | `SchedulerIntentAndAbortTests`（含 Confirm / Skip 重放） |
| 仿真按设备行启用 | `PlcLoopbackGate`、`PlcLoopbackGateTests`；`Seed:Demo` 见 README「启动引导配置」 |
