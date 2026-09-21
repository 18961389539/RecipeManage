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

## 启动引导配置

| 配置 | 默认 | 说明 |
| --- | --- | --- |
| `Seed:Demo` | 开发环境 `true`，其它 `false` | 为 `false` 时只写入最小骨架（账号 / 主设备 / 首套配方 / 设备类库），不写入仿真从站与示范配方 |
| `Seed:AdminPassword` | 无 | 初始账号口令。未配置且 `Seed:Demo=false` 时随机生成，只出现在启动日志（warn 级）里一次 |

升级涉及的数据修复（点表补齐、乱码修复、ISA-88 补齐、Heat 升温时长补齐）由 `applied_data_fixes` 表记录，
每个修复只执行一次，不再随每次启动重复改写业务数据；其中对已发布配方的自动改动会写入审计日志。 仿真器。
