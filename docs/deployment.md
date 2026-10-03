# 单机部署（一台设备 / 一台现场 PC）

这套系统的运行时假设是**单进程、单数据库文件、单台被控设备**。本文只讲怎么把它装成一台能被自己照顾过来的现场机器。
功能层面的留存/备份策略见代码内注释（`Infrastructure/Persistence/DatabaseBackup.cs`）。

## 1. 为什么必须有这几样

| 风险 | 没有防线时的后果 | 防线 |
| --- | --- | --- |
| 两个实例同时驱动一台设备 | 两边都往同一台 PLC 写参数、置 Trigger；`ConcurrencyStamp` 守卫只会让其中一方在写了一半之后把批次打成 `Faulted`。设备租约的唯一索引只挡"启动新批次"，挡不住两个进程各自 `RecoverRunningAsync` 接管同一批次 | `SingleInstanceLock`：拒绝第二个实例启动，退出码 **75** |
| 进程崩了 / 机器重启后没人启动它 | 现场表现为"界面打不开"，而且没有任何地方留下原因 | Windows 服务（开机自启 + SCM 崩溃重启）+ 每分钟探活的看门狗 |
| 进程活着但引擎卡住（PLC socket 挂死、循环死锁） | 任务管理器看着完全正常，产线其实已经没人监督 | 看门狗探 `/health`，这是 SCM 看不见的失效模式 |
| 升级改坏了库结构 | 迁移是手写的，`Down()` 没人调用，库停在半应用状态；设备可能正在跑批 | `SchemaBootstrap` 在有 pending 迁移时**先落一份已校验快照**，写不成就不迁移（§5） |
| 现场崩了却拿不到证据 | 只有 console provider 的话，服务形态下什么都不会留下 | `App_Data/logs/brmes-<UTC 日期>.log`（§5）+ `/health` 里的 `version` / `migration` |

## 2. 单实例互斥

- 锁的是**数据库文件**，不是机器：在 `<db>.lock`（如 `App_Data/recipes.db.lock`）上持有一个 `FileShare.Read` 的读写独占句柄。
  因此同一台机器上两份 checkout（各自的 `App_Data`）可以并行跑，开发机不会被挡。
- 判据是**独占句柄**，不是锁文件存不存在。进程被硬杀时操作系统自动释放句柄，所以不会出现"上次崩了，留下文件，从此再也起不来"。
  锁文件因此故意不删，里面只有诊断信息（`pid=…  started=…  machine=…  db=…`）。
- 抢锁发生在建库、迁移、调度器恢复**之前**：这三件事任何一件在两个进程里同时跑，后果都不是报错而是静默写坏数据。
- 内存库（设计期工具、部分测试）没有可锁的文件 → 直接放行，不会变成"起不来"。
- 拒绝启动时写 `crit` 日志并点名当前持有者，退出码 `75`（`EX_TEMPFAIL`）。**看门狗依赖这个码区分"被拒"与"崩了"**。
- 确认没有活实例但锁仍拿不到时（极少数：文件被别的程序独占），删掉 `App_Data/recipes.db.lock` 即可。

### 2.1 库文件的形态：WAL 与三个文件

库跑在 **WAL 日志模式**（`SchemaBootstrap` 在迁移之后切换，写进库文件，旧版装机的库升级时自动转换，无需人工操作）。
原因：调度车道每个 tick 都在写、界面同时在轮询读，回滚日志模式下读者会挡住写者提交；WAL 下读写互不等待。
写者仍然只有一个，所以每条连接另有 `busy_timeout=10000`（`SqliteConnectionPragmas`）。

后果是 `App_Data` 里**一个库会表现为三个文件**，运行期间都是库的一部分：

| 文件 | 是什么 | 注意 |
| --- | --- | --- |
| `recipes.db` | 主库 | **单独拷它得到的是撕裂的库**：最近的提交可能还在 `-wal` 里 |
| `recipes.db-wal` | 尚未合并进主库的提交 | 不要删、不要单独移走；进程正常退出时会合并并删除 |
| `recipes.db-shm` | 共享内存索引 | 可重建，但进程运行时不能动 |

- **要一份可拷走的库，用备份，不要拷文件。** 每日备份与升级前快照走 SQLite Backup API，产物是自包含的**单个文件**（`brmes-<utc>.db`，已退回回滚模式、不带 `-wal`/`-shm`）；
  管理员也可以在「用户与备份」页下载。
- **库文件不能放在网络共享盘上**（SMB / NFS）：WAL 靠本机共享内存协调。放上去时切换会失败，日志出现
  “数据库没能切到 WAL 模式”，系统退回回滚模式继续跑，但读写并发会变差。
- **杀毒 / 同步盘**要把整个 `App_Data` 排除（包括 `-wal` / `-shm`），见 §8。
- 每天备份之后的维护如果做了 VACUUM，会立刻做一次截断检查点，`-wal` 不会跟着涨到和库一样大。

## 3. 安装

**不需要**在目标机上装 .NET SDK：`--self-contained` 的包在开发机上出，现场只解包。

### 3.1 推荐：开发机出包，现场解包

```powershell
# 开发机：出版本（-Version 覆盖 Directory.Build.props 里的缺省值 1.0.0）
pwsh -File deploy\publish-package.ps1 -Version 1.0.1
#   -> artifacts\brmes-1.0.1+<短提交号>-win-x64.zip，另有同名 .sha256
#   -> 压缩包里含 watchdog.ps1 与 package-manifest.json（版本、完整提交号、构建时刻、rid）

# 现场机：核对哈希后解包安装（先带 -WhatIf 看一遍）
Get-FileHash .\<包名>.zip -Algorithm SHA256      # 应与同名 .sha256 里那串一致
pwsh -File deploy\install-watchdog.ps1 -PackagePath .\brmes-1.0.1+abc12345-win-x64.zip `
    -PublishTo C:\brmes -JwtKey "<至少 32 字节，现场生成>" -BackupKeep 7
```

跨版本升级请加 `-Clean`：它只删 `C:\brmes` 里除 `App_Data` 与 `logs` 之外的旧文件（库文件永远不碰）。
不加就是覆盖式解包，会留下上一版的孤儿文件（原生依赖与 appsettings 段最常见）。

出包时版本被同时写进 `Version` 与 `InformationalVersion`，后者带 `+提交号`，于是
`/health` 的 `version` 字段与 `package-manifest.json` 说的是同一件事——**远程支持的第一句话
可以是"打开 http://localhost:5010/health，把 version 念给我"**，不需要人到现场。

### 3.2 备选：现场机上直接发布（需要该机有 SDK）

```powershell
pwsh -File deploy\install-watchdog.ps1 -PublishTo C:\brmes -JwtKey "<至少 32 字节，现场生成>" -BackupKeep 7
```

`install-watchdog.ps1` 做的事，全部支持 `-WhatIf` 先看一遍：

1. 解包（`-PackagePath`）或 `dotnet publish -c Release -r win-x64 --self-contained true` 到 `C:\brmes`；
2. 写 `appsettings.Production.json`（连接串、`Jwt:Key`、`Backup:*`、`Maintenance:*`、`Logging:File:*`）；
3. `sc create BRMES`，`start= auto`，`sc failure … restart/60000` 三次退避后保持停止；
   并通过服务的环境变量固定 `ASPNETCORE_ENVIRONMENT=Production`、`ASPNETCORE_URLS`、`BRMES_WINDOWS_SERVICE=1`；
4. 注册计划任务 `BRMES-Watchdog`，每分钟跑一次 `deploy\watchdog.ps1`。

不想用 Windows 服务就把 `-ServiceName ''` 传空：看门狗会直接用 `Start-Process` 拉起 exe。

### 三个必须钉住的路径

`ConnectionStrings:Sqlite` 默认值是**相对路径** `Data Source=App_Data/recipes.db`。它相对于内容根解析，所以：

- 服务：`sc create` 的 `binPath` 带 `--contentRoot "C:\brmes"`；
- 看门狗：`Start-Process` 带 `--contentRoot`；
- 手工：`cd C:\brmes` 后再启动。

漏掉任何一处，进程会在自己的启动目录下**另建一个空库**——这是最难发现的一种事故（界面一切正常，只是那是另一个数据库）。

### 为什么 `BRMES_WINDOWS_SERVICE=1` 要显式给

`UseWindowsService()` 无条件调用会把内容根改成 exe 所在目录，于是相对 `App_Data` 落到 `bin\` 里。
只有确实作为服务运行时才打开它。

### 不用安装脚本时：仓库里的 `appsettings.Production.json` 模板

`src/RecipesManage.Api/appsettings.Production.json` 随包一起发布，是给手工部署的起点。它的行为是**故意起不来**的：

- `Jwt:Key` 是 `CHANGE-ME-…` 占位值，非开发环境一律拒绝启动（占位密钥等于任何人都能离线伪造管理员令牌）。
  换成自己的密钥，或者干脆从文件里删掉这一项、改走环境变量 `Jwt__Key`。
- `Seed:Demo=false`：不写演示资产。这一份之外没有别处需要改——`Seed:Demo` 缺省值就是"仅开发环境为真"。
- `Jwt:ExpireHours=8`：与一个班次对齐。默认 12 小时是跨班次的——早班 07:00 登录的令牌能活到夜里 19:00，
  而"谁签的这一步"在 21 CFR 11 里是按时刻成立的事实，跨班存活让令牌与在岗人对不上。
  注意角色每次请求都由 `CurrentUserMiddleware` 按库内当前用户复核，所以缩短过期**不影响**权限变更的生效速度，
  只是让人重新登录一次。两三人厂嫌烦就调回 12。
- 审批链另外预置了一条 **`single-review`（精简单审：提交 + 质量一道签核）**，`IsDefault=false`。
  三级链是按"工艺工程师 / 工艺主管 / 质量 三个岗位三个人"写的，而域层禁止同一人签两道——2~3 人的厂
  要么天天卡在自己的审批台上，要么去建共用账号自签自放（后者是把职责分离搬进假山里）。
  预设给的是合规退路：**提交人与签核人仍然必须是两个人，那条规则一行没松**。
  要用它的人在「审批链配置」里选它，或按配方逐条指定；现有客户默认继续走三级，没人被这条预设推着改流程。
- `Backup:Directory` 沿用 `App_Data/backups`，**必须**改成另一块盘，见 §8。

## 4. 看门狗行为契约（`deploy\watchdog.ps1`）

退出码就是计划任务里的"上次结果"：

| 码 | 含义 |
| --- | --- |
| `0` | 健康；或本次修复成功 |
| `1` | 探活失败且没能修复（含处于重启冷却期） |
| `2` | 看门狗自身异常（必须当作故障看，不能当"无事发生"） |

其它约定：

- **只杀它自己启动的那种进程**：`Get-Process` 结果还要用可执行文件全路径过滤，开发机上另一个 `dotnet run` 不是它的目标。
- 服务已注册时用 `Restart-Service`（让 SCM 保持所有权），否则 `Stop-Process` + `Start-Process`。
- 重启后轮询 `/health` 直到起来或超过 `-SettleSec`。
- 子进程以 **75** 退出 → 记 `startup refused: another instance holds the database lock`，**不重试**，等冷却期过。
- 冷却期（默认 180s）防止崩溃循环反复朝 PLC 发起握手；状态记在日志同目录的 `watchdog.state`。
- 健康时默认**不写日志**（`-VerboseHeartbeat` 可强制），日志超 1MB 转一代。脚本正文刻意用英文：
  Windows PowerShell 5.1 按 ANSI 读无 BOM 的 UTF-8，中文注释和日志会成乱码。

## 5. 开机时才发生的三件事

现场机器不是 7×24 的，这两条都是为"晚上关着"设计的：

- **错过的备份会补跑**。每天 `Backup:AtUtc` 那一班如果机器正关着，启动时先看最近一个**已过去**的计划时刻在备份目录里
  有没有对应快照（判据只看文件名 `brmes-yyyyMMdd-HHmmss.db` 里的日期，不看文件修改时间），没有就立刻补一班，
  随后照常按排程等下一次。缺几天也只补一份——这是每日快照，不是要补齐历史。首次建库同样会在开机时立刻落一份，
  所以新机器不会带着"零备份"跑一整天。判据在 `Infrastructure/Persistence/DatabaseBackup.cs` 的 `NeedsCatchUp`。
- **环回仿真器按需才起来**。Modbus（1502）/ S7（1102）/ OPC UA（48410）三个本机从站只在**存在一条启用设备行
  把 `MB-01` / `S7-01` / `UA-01` 指向对应协议 + 对应端口 + 回环地址**时才绑端口；否则整个从站不启动
  （OPC UA 连 PKI 目录和发布定时器都不会建）。`Seed:Demo=false` 时这三条设备行本来就不会写入，所以生产机器
  默认一个仿真端口都不占；而协议为 `Simulator`（进程内仿真，不走 TCP）或指向真实 PLC IP 的设备，
  也不会被误当成"环回在跑"。判据在 `Simulation/PlcLoopbackGate.cs`。
  注意这个判据只在**启动时**跑一次：界面上把 `MB-01` 停用或改成真实 PLC 地址，要重启进程才会把端口放掉
  （反过来也一样，新建设备行后得重启才有从站）。
- **改库结构之前先落一份已校验的升级前快照**。有 pending 迁移、且库里已经有表时，`SchemaBootstrap.ApplyAsync`
  会先把当前库快照到 `App_Data/backups/pre-migration/`（跑 `integrity_check`、退回单文件），**快照写成就迁移，
  写不成就拒绝迁移**——宁可停在旧版也不要一个没有退路的库。全新空库不在此列（没有旧结构可破坏）。
  这个子目录不在每日备份的列表与 `Backup:Keep` 裁剪范围内，所以退路不会被日常轮换顺手裁掉；
  留几份由 `Backup:KeepPreMigration`（默认 3）决定。
  这条防线的存在理由：本项目的迁移是**手写**的、`Down()` 没有任何调用路径，所以"回滚"实际上等于"恢复快照"。
  日志里那句 `即将应用 N 条迁移…升级前快照 <文件名> 已校验` 就是恢复时要用的文件名。

## 6. 电话里能问出来的东西（不用人到现场）

| 要知道的事 | 去哪儿看 |
| --- | --- |
| 装的是哪一版、库结构升到哪儿了 | `curl http://localhost:5010/health` → `version`（含 `+提交号`）、`migration`（最后一条已应用迁移）、`pendingMigrations`（正常恒为 0，非 0 = 升级半途失败）。界面上运行总览的健康徽标悬停也是这两行。 |
| 进程活过几次、为什么进 Fault | `App_Data\logs\brmes-<yyyyMMdd>.log`（UTC 命名的当天文件） |
| 有没有可恢复的退路 | `App_Data\backups\`（每日快照）与 `App_Data\backups\pre-migration\`（升级前快照） |

日志的口径：按 **UTC 日期**一天一个文件，行首是定宽 UTC 时间戳（与审计时间、备份文件名同一个口径，
两份文件对时间轴不用换算）；默认 `Information` 起，默认**不删旧文件**（`Logging:File:RetainedDays` 显式配了才裁）。
写不进磁盘（盘满、目录被改权限）时这道 sink 自己停掉，绝不把业务线程拖停，也绝不抛穿宿主。

## 7. 装完必须验的四件事

```powershell
curl http://localhost:5010/health                 # 1) status ok，且带 pid / startedAtUtc
# 2) 手工再启一个实例 -> 立刻退出，退出码 75，日志点名持有者
cd C:\brmes && RecipesManage.Api.exe --urls http://localhost:5011 --contentRoot C:\brmes
echo $LASTEXITCODE                                 #    期望 75
schtasks /run /tn BRMES-Watchdog                  # 3) 健康时不该有任何动作（也不该写日志）
taskkill /im RecipesManage.Api.exe /f             # 4) 一分钟内应被看门狗拉回，watchdog.log 记 "repaired"
```

`/health` 里的 `pid` + `startedAtUtc` 就是为第 4 步准备的：探活通过但 `pid` 变了，说明刚被重启过，
而界面上完全看不出来。

## 8. 这台机器还要顺手做的（脚本没覆盖）

- **时间同步**：审计时间、快照冻结时间、备份文件名全是本机 UTC 时钟。现场机器不走 NTP，21 CFR 11 的时间履历就失真。
- **杀毒/同步盘排除**：把 `C:\brmes\App_Data` 排除在实时扫描与 OneDrive 同步之外，否则 SQLite 会撞 `BUSY` 甚至撕裂。
- **备份离盘**：`App_Data/backups` 与库在同一块盘上，盘坏了 7 份快照一起没。把该目录纳入机器自己的复制/备份计划，或把 `Backup:Directory` 指到第二块盘。
- **磁盘余量**：过程样本约 2.16 万行/小时（3 测点、400ms 节流），加上每天一份全库快照。
  库自己的维护已经自动化了：每天备份之后跑一轮 `PRAGMA optimize`，并且**只在没有批次在跑、
  且空闲页够多**时才 `VACUUM`（SQLite 删行只把页挂到 freelist，文件永不自缩）。
  阈值在 `Maintenance:MinFreeRatio` / `MinFreeMegabytes`；管理员也能在「用户与备份」页点「立即维护」当场跑一轮。
  顺序是刻意的：VACUUM 要重写整个库文件，所以手里必须已有当天刚验过的快照。
- **恢复演练**：停应用 → 把 `recipes.db` **连同 `recipes.db-wal`、`recipes.db-shm`** 一起改名保留（或移走）→ 复制一份 `brmes-<utc>.db` 过去命名为 `recipes.db` → 起应用 → `/health` + 打开一个历史批记录。**没演过的备份不算备份。**
  必须清掉旧的 `-wal` / `-shm`：留着它们，SQLite 会把旧库的日志套到刚恢复的库上，轻则丢数据，重则打不开。
