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
