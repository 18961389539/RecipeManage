# 单机部署（一台设备 / 一台现场 PC）

这套系统的运行时假设是**单进程、单数据库文件、单台被控设备**。本文只讲怎么把它装成一台能被自己照顾过来的现场机器。
功能层面的留存/备份策略见代码内注释（`Infrastructure/Persistence/DatabaseBackup.cs`）。

## 1. 为什么必须有这两样

| 风险 | 没有防线时的后果 | 防线 |
| --- | --- | --- |
| 两个实例同时驱动一台设备 | 两边都往同一台 PLC 写参数、置 Trigger；`ConcurrencyStamp` 守卫只会让其中一方在写了一半之后把批次打成 `Faulted`。设备租约的唯一索引只挡"启动新批次"，挡不住两个进程各自 `RecoverRunningAsync` 接管同一批次 | `SingleInstanceLock`：拒绝第二个实例启动，退出码 **75** |
| 进程崩了 / 机器重启后没人启动它 | 现场表现为"界面打不开"，而且没有任何地方留下原因 | Windows 服务（开机自启 + SCM 崩溃重启）+ 每分钟探活的看门狗 |
| 进程活着但引擎卡住（PLC socket 挂死、循环死锁） | 任务管理器看着完全正常，产线其实已经没人监督 | 看门狗探 `/health`，这是 SCM 看不见的失效模式 |

## 2. 单实例互斥

- 锁的是**数据库文件**，不是机器：在 `<db>.lock`（如 `App_Data/recipes.db.lock`）上持有一个 `FileShare.Read` 的读写独占句柄。
  因此同一台机器上两份 checkout（各自的 `App_Data`）可以并行跑，开发机不会被挡。
- 判据是**独占句柄**，不是锁文件存不存在。进程被硬杀时操作系统自动释放句柄，所以不会出现"上次崩了，留下文件，从此再也起不来"。
  锁文件因此故意不删，里面只有诊断信息（`pid=…  started=…  machine=…  db=…`）。
- 抢锁发生在建库、迁移、调度器恢复**之前**：这三件事任何一件在两个进程里同时跑，后果都不是报错而是静默写坏数据。
- 内存库（设计期工具、部分测试）没有可锁的文件 → 直接放行，不会变成"起不来"。
- 拒绝启动时写 `crit` 日志并点名当前持有者，退出码 `75`（`EX_TEMPFAIL`）。**看门狗依赖这个码区分"被拒"与"崩了"**。
- 确认没有活实例但锁仍拿不到时（极少数：文件被别的程序独占），删掉 `App_Data/recipes.db.lock` 即可。

## 3. 安装

前提：现场 PC 是 Windows；**不需要**装 .NET SDK（`--self-contained` 发布）。

```powershell
# 一次性：安装服务 + 注册看门狗计划任务（会写 appsettings.Production.json）
pwsh -File deploy\install-watchdog.ps1 -PublishTo C:\brmes -JwtKey "<至少 32 字节，现场生成>" -BackupKeep 7
```

脚本做的事，全部支持 `-WhatIf` 先看一遍：

1. `dotnet publish -c Release -r win-x64 --self-contained true` 到 `C:\brmes`；
2. 写 `appsettings.Production.json`（连接串、`Jwt:Key`、`Backup:*`）；
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

## 5. 装完必须验的四件事

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

## 6. 这台机器还要顺手做的（脚本没覆盖）

- **时间同步**：审计时间、快照冻结时间、备份文件名全是本机 UTC 时钟。现场机器不走 NTP，21 CFR 11 的时间履历就失真。
- **杀毒/同步盘排除**：把 `C:\brmes\App_Data` 排除在实时扫描与 OneDrive 同步之外，否则 SQLite 会撞 `BUSY` 甚至撕裂。
- **备份离盘**：`App_Data/backups` 与库在同一块盘上，盘坏了 7 份快照一起没。把该目录纳入机器自己的复制/备份计划，或把 `Backup:Directory` 指到第二块盘。
- **磁盘余量**：过程样本约 2.16 万行/小时（3 测点、400ms 节流），加上每天一份全库快照。
- **恢复演练**：停应用 → 把 `recipes.db` 改名保留 → 复制一份 `brmes-<utc>.db` 过去命名为 `recipes.db` → 起应用 → `/health` + 打开一个历史批记录。**没演过的备份不算备份。**
