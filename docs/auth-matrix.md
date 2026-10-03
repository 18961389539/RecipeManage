# 授权矩阵

谁能调哪个接口。**下面两张表是从代码生成的**（`tests/RecipesManage.Api.Tests/AuthMatrixTests.cs`），代码与本文不一致时测试失败：
改了角色名单或接口门槛，就设环境变量 `UPDATE_AUTH_MATRIX=1` 跑一次该测试重新生成，再看 `git diff` 确认授权变更正是你要的。

## 两道门

| 层 | 在哪 | 作用 |
| --- | --- | --- |
| 第一道门 | 控制器上的 `[Authorize(Policy = …)]` | 进业务逻辑之前拒绝，OpenAPI 里可见 |
| 第二道门 | Application 服务里的 `EnsureCan(Capabilities.X)` | 服务被别处直接调用（调度器、测试、后续的其它入口）时仍然自保；电子签名路径不能只靠一层 |

两道门读同一份名单：`Domain/Identity/Capabilities.cs`。改权限只改那个文件。

## 规则（有测试钉住）

- 名单是**闭集**：不在里面的角色一律不行，没有"管理员通配"。管理员不碰产线（不能启停、跳步、放行）。
- 职责分离：放行 / 拒收 / 实验室样品判定**仅质量**；跳步**仅主管**；设备与握手点表**仅管理员**。见 `CapabilitiesTests`。
- 每个接口必须声明门槛；没有 `[Authorize]` 也没有 `[AllowAnonymous]` 的接口等于对所有人开放，`AuthMatrixTests` 会拦下。
- 匿名接口与"只要登录就能写"的接口各是一张封闭白名单，新增必须在测试里登记并写明理由。
- 令牌里的角色只是签发时的快照，`CurrentUserMiddleware` 按库内当前用户复核后才进授权（停用 / 改角色立即生效）。

## 不在表里的两处

- **`/health`（根路径）**：`Program.cs` 里的最小 API，匿名；与表里的 `/api/health` 内容一致，供看门狗与外部探活。
- **`/hubs/execution`（SignalR）**：`ExecutionHub` 为 `[Authorize]`，登录即可订阅；只推送、不接受写操作。

## 读接口：目前对任何已登录用户开放

表里所有 `GET`（除 `admin` 名下的用户 / 备份 / 维护 / 审批链配置之外）都只要求登录，服务层也没有二次校验。
这包括 `GET /api/audit`（全局审计日志）、批记录与 PDF、配方、批次、物料谱系。这是**现状，不是评审过的决定**：
GMP 场景里"谁能看审计日志 / 批记录"通常要单独定，若要收紧，在 `Capabilities` 加能力、控制器挂策略，本表会随之变。

## 有意只要求"登录即可"的写接口

- `POST /api/recipes/{id}/decide`：合法角色取决于配方当前停在哪个审核节点（运行时才定），静态能力表表达不了。
  `RecipeApprovalService` 按节点 `RequiredRole` 判定，并检查同一人不得担任同一版本的多个节点。

<!-- BEGIN:generated (由 AuthMatrixTests 生成，勿手改) -->

### 能力 × 角色

| 能力 | 管理员 | 工艺工程师 | 主管 | 质量 | 操作员 |
| --- | :-: | :-: | :-: | :-: | :-: |
| `admin` | ✓ |  |  |  |  |
| `recipe.author` |  | ✓ |  |  |  |
| `recipe.export` |  | ✓ | ✓ | ✓ |  |
| `batch.operate` |  |  | ✓ |  | ✓ |
| `batch.skip` |  |  | ✓ |  |  |
| `batch.confirm` |  |  | ✓ | ✓ | ✓ |
| `quality.disposition` |  |  |  | ✓ |  |
| `alarm.ack` |  |  | ✓ | ✓ | ✓ |
| `lot.receive` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `lot.handle` | ✓ |  | ✓ | ✓ | ✓ |
| `equipment.admin` | ✓ |  |  |  |  |
| `equipment.operate` | ✓ |  | ✓ |  | ✓ |
| `equipment.simulate` |  |  | ✓ |  | ✓ |
| `phase.library` | ✓ | ✓ |  |  |  |

### 接口 × 门槛

| 方法 | 路径 | 门槛 | 允许的角色 |
| --- | --- | --- | --- |
| POST | `/api/admin/sqlite-backup` | `admin` | 管理员 |
| GET | `/api/alarms` | 登录即可 | 任何已登录用户 |
| POST | `/api/alarms/{id:guid}/ack` | `alarm.ack` | 主管、质量、操作员 |
| GET | `/api/approval-chains` | 登录即可 | 任何已登录用户 |
| POST | `/api/approval-chains` | `admin` | 管理员 |
| DELETE | `/api/approval-chains/{id:guid}` | `admin` | 管理员 |
| GET | `/api/audit` | 登录即可 | 任何已登录用户 |
| POST | `/api/auth/login` | 匿名 | 任何人 |
| GET | `/api/auth/me` | 登录即可 | 任何已登录用户 |
| GET | `/api/batches` | 登录即可 | 任何已登录用户 |
| POST | `/api/batches` | `batch.operate` | 主管、操作员 |
| GET | `/api/batches/{id:guid}` | 登录即可 | 任何已登录用户 |
| POST | `/api/batches/{id:guid}/abort` | `batch.operate` | 主管、操作员 |
| GET | `/api/batches/{id:guid}/alarms` | 登录即可 | 任何已登录用户 |
| POST | `/api/batches/{id:guid}/confirm` | `batch.confirm` | 主管、质量、操作员 |
| GET | `/api/batches/{id:guid}/handshake-log` | 登录即可 | 任何已登录用户 |
| POST | `/api/batches/{id:guid}/hold` | `batch.operate` | 主管、操作员 |
| GET | `/api/batches/{id:guid}/lab-samples` | 登录即可 | 任何已登录用户 |
| POST | `/api/batches/{id:guid}/lab-samples` | `lot.handle` | 管理员、主管、质量、操作员 |
| POST | `/api/batches/{id:guid}/lab-samples/{sampleId:guid}/disposition` | `quality.disposition` | 质量 |
| GET | `/api/batches/{id:guid}/record` | 登录即可 | 任何已登录用户 |
| GET | `/api/batches/{id:guid}/record.pdf` | 登录即可 | 任何已登录用户 |
| POST | `/api/batches/{id:guid}/reject-disposition` | `quality.disposition` | 质量 |
| POST | `/api/batches/{id:guid}/release` | `quality.disposition` | 质量 |
| POST | `/api/batches/{id:guid}/resume` | `batch.operate` | 主管、操作员 |
| GET | `/api/batches/{id:guid}/samples` | 登录即可 | 任何已登录用户 |
| POST | `/api/batches/{id:guid}/skip` | `batch.skip` | 主管 |
| GET | `/api/batches/{id:guid}/snapshot-drift` | 登录即可 | 任何已登录用户 |
| POST | `/api/batches/{id:guid}/start` | `batch.operate` | 主管、操作员 |
| GET | `/api/dashboard` | 登录即可 | 任何已登录用户 |
| GET | `/api/equipment` | 登录即可 | 任何已登录用户 |
| POST | `/api/equipment` | `equipment.admin` | 管理员 |
| GET | `/api/equipment/classes` | 登录即可 | 任何已登录用户 |
| POST | `/api/equipment/classes/{classId:guid}/templates` | `phase.library` | 管理员、工艺工程师 |
| DELETE | `/api/equipment/classes/{classId:guid}/templates/{id:guid}` | `phase.library` | 管理员、工艺工程师 |
| PUT | `/api/equipment/classes/{classId:guid}/templates/{id:guid}` | `phase.library` | 管理员、工艺工程师 |
| PUT | `/api/equipment/{id:guid}` | `equipment.admin` | 管理员 |
| POST | `/api/equipment/{id:guid}/inject-fault` | `equipment.simulate` | 主管、操作员 |
| POST | `/api/equipment/{id:guid}/test-connection` | `equipment.operate` | 管理员、主管、操作员 |
| POST | `/api/equipment/{id:guid}/validate-tagmap` | `equipment.admin` | 管理员 |
| GET | `/api/health` | 匿名 | 任何人 |
| GET | `/api/lots` | 登录即可 | 任何已登录用户 |
| POST | `/api/lots` | `lot.receive` | 管理员、工艺工程师、主管、质量、操作员 |
| GET | `/api/lots/{id:guid}/genealogy` | 登录即可 | 任何已登录用户 |
| POST | `/api/lots/{id:guid}/split` | `lot.handle` | 管理员、主管、质量、操作员 |
| GET | `/api/recipes` | 登录即可 | 任何已登录用户 |
| POST | `/api/recipes` | `recipe.author` | 工艺工程师 |
| GET | `/api/recipes/export` | `recipe.export` | 工艺工程师、主管、质量 |
| POST | `/api/recipes/import` | `recipe.author` | 工艺工程师 |
| GET | `/api/recipes/{id:guid}` | 登录即可 | 任何已登录用户 |
| PUT | `/api/recipes/{id:guid}` | `recipe.author` | 工艺工程师 |
| PUT | `/api/recipes/{id:guid}/approval-chain` | `recipe.author` | 工艺工程师 |
| GET | `/api/recipes/{id:guid}/compare` | 登录即可 | 任何已登录用户 |
| POST | `/api/recipes/{id:guid}/decide` | 登录即可 | 任何已登录用户 |
| POST | `/api/recipes/{id:guid}/new-version` | `recipe.author` | 工艺工程师 |
| PUT | `/api/recipes/{id:guid}/procedure` | `recipe.author` | 工艺工程师 |
| POST | `/api/recipes/{id:guid}/reopen` | `recipe.author` | 工艺工程师 |
| POST | `/api/recipes/{id:guid}/submit` | `recipe.author` | 工艺工程师 |
| GET | `/api/system/backups` | `admin` | 管理员 |
| POST | `/api/system/backups` | `admin` | 管理员 |
| POST | `/api/system/maintenance` | `admin` | 管理员 |
| GET | `/api/users` | `admin` | 管理员 |
| POST | `/api/users` | `admin` | 管理员 |
| PUT | `/api/users/{id:guid}` | `admin` | 管理员 |

<!-- END:generated -->
