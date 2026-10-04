# 工艺配方管理与实时执行系统（BRMES）

面向离散制造的 Batch Recipe Management & Execution System。本仓库**不使用 Docker**，开发与运行数据库为 **SQLite**（`src/RecipesManage.Api/App_Data/recipes.db`）。

## 运行方式

终端 1：

```bash
dotnet run --project src/RecipesManage.Api --launch-profile http
```

终端 2：

```bash
cd frontend
npm install
npm run dev
```

浏览器打开 `http://localhost:5173`。

演示账号：

- `admin` / `Admin@123`
- `engineer` / `Engineer@123`
- `supervisor` / `Supervisor@123`
- `qa` / `Quality@123`
- `operator` / `Operator@123`

种子数据含已批准主配方 `AL-HT-T6` 与仿真设备 `HT-01`。用操作员创建批次并启动后，后台调度引擎会按四步握手驱动进程内 PLC 仿真器。

## 验证

回归一条命令（后端四个测试项目 + 前端单测 + 类型检查/构建）；加 `-WithE2e` 再起 API 跑冒烟与 Playwright 全套：

```powershell
powershell -File verify.ps1
powershell -File verify.ps1 -WithE2e
```

跑之前先关掉手动启动的 `dotnet run` API：`dotnet test` 会重建 Api，正在运行的进程会锁住输出文件。

## 架构约束

- **单进程、单实例**：同一份数据库只允许一个应用进程，第二个会被拒绝启动（退出码 75）。
- **SQLite（WAL 模式）**，单文件；备份用内置备份接口，不要直接拷运行中的 `.db`。
- **调度在进程内**（内存通道 + 单读者），不支持水平扩展；操作员指令先落库再入队，重启可恢复。
- **仿真 PLC 默认关**：按库里的设备行决定是否绑端口，生产机零占用。

理由、代价与"什么时候该重新评估"见 [ADR 0001](docs/adr/0001-runtime-boundaries.md)；部署与运维见 [docs/deployment.md](docs/deployment.md)；授权矩阵见 [docs/auth-matrix.md](docs/auth-matrix.md)。

## 配方模型的两条硬约束

都在**提交审核**时就拒绝，不留到批次上——批准之后控制配方就密封了，那时候才发现的代价是一条已放行的配方根本跑不了。

- **标记"归档作质量判定"的参数必须有实测来源**：要么声明实测点（设备点表 `Measured` 的键名），要么落在可推断的三类量上
  （时长→`HoldTime`、温度→`Temperature`、压力→`Pressure`）。以前推不出来源的后果**不是报错**，而是这条规格永远归档不到值
  → 每批都被判超差 → 质量随手写一句"检验合格"就能偏差放行，履历上看不出它从没被评价过
  （见 `QualityArchive.DemandArchivableSources`）。硬度这类实验室指标请改建**质检样品（LIMS）**，不要标归档；
  新增的**实测值**语义就是"这个数必须去实测点取"，选它就必须填实测点。
- **单个工步的工艺时长必须写得进 PLC 时长槽**：`0.2 秒 – 2 小时`（`ProcessDuration.Min/MaxWritableSeconds`）。
  单位认 ms / s / min / h / d（中英文都认，换算表与"算不算时长"的识别表同源）。超上限以前会被 `Math.Clamp`
  静默截成 7200 秒发给 PLC，而上位机仍按原始时长等——设定值被改了，履历上什么都看不出来。
  要跑 24 小时固化这类工艺，请分解成多条工步（或换能表达长时长的 PLC 程序）。

## 启动引导配置

| 配置 | 默认 | 说明 |
| --- | --- | --- |
| `Seed:Demo` | 开发环境 `true`，其它 `false` | 为 `false` 时只写入最小骨架（账号 / 主设备 / 首套配方 / 设备类库），不写入仿真从站与示范配方 |
| `Seed:AdminPassword` | 无 | 初始账号口令。未配置且 `Seed:Demo=false` 时随机生成，只出现在启动日志（warn 级）里一次 |

升级涉及的数据修复（点表补齐、乱码修复、ISA-88 补齐、Heat 升温时长补齐）由 `applied_data_fixes` 表记录，
每个修复只执行一次，不再随每次启动重复改写业务数据；其中对已发布配方的自动改动会写入审计日志。 仿真器。
