# 外部审计锚部署与运行

## 目标与边界

BRMES 每日从同一个 SQLite 读事务中分别计算 `audit_logs` 与 `signature_records` 的规范化 Merkle 根，并只把根清单、行数、来源/快照标识、产品版本和迁移水位发送到异机接收端。审计明文、签名明文和数据库文件不会发送到接收端。

接收端验证清单格式，以自身证书私钥签署带单调序号和前序回执摘要的回执，并将清单及回执追加到独立 SQLite ledger。BRMES 只持有 bearer token 和用于验证回执的公开证书，不持有签名私钥。

这构成独立机器上的**可检测篡改基线**，不是硬件 WORM 或绝对不可改写存储：

- SQLite `UPDATE` / `DELETE` 触发器可以阻止正常 SQL 写路径修改回执，但接收端数据库管理员仍能移除触发器、替换数据库或还原旧备份。
- 首次锚定只能确立首次接收时的基线，不能证明首次锚定以前的历史没有被改写。
- 两条数据流分别锚定；它们没有共享主键，不代表逐条一一对应。
- 快照描述执行时两张表的当前内容，不是审计归档，也不能还原签署当时的全部业务证据。表中时间字段不是数据库提交序号。
- 若需抵御接收端主机管理员，须另加独立管理员控制的 WORM/Object Lock、远端备份或第三方透明日志；本服务本身不提供该等级保证。

## 信任域与凭证

建议由不同管理员维护 BRMES 主机与接收端主机。接收端至少应运行在另一台 Windows 主机上；接收端 Windows 服务身份只获得：

- 接收端程序目录的读取/执行权限。
- `DatabasePath` 所在目录的修改权限。
- 签名证书私钥的读取权限。
- TLS 证书私钥的读取权限（若由 Kestrel 终止 TLS）。

回执签名证书与 HTTPS TLS 证书建议分开。配置中的 `CertificateThumbprint` 指**回执签名证书**，而 BRMES 的 `VerificationCertificatePath` 指这张证书的 PEM 公钥材料；TLS 证书只负责 HTTPS 传输。回执 `KeyId` 是签名证书 DER 字节的 SHA-256，不是证书存储区 thumbprint。

为接收端 bearer token 生成至少 32 字节的随机值，并通过受控渠道分别配置在两端。不得将真实 token、证书私钥、PFX 密码或生产配置提交到源码仓库、安装包、普通工单或日志。生产配置文件应使用 NTFS ACL 限制到对应服务身份和受控管理员。BRMES 安装器当前不会自动生成或分发审计锚凭证。

## 接收端部署

在独立 Windows Server 上以受控发布机生成 self-contained 程序目录；目标机无需安装 .NET：

```powershell
dotnet publish src\RecipesManage.AuditAnchor.Server\RecipesManage.AuditAnchor.Server.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
  -o .\artifacts\audit-anchor
```

将发布目录复制到接收端，例如 `C:\Program Files\BRMES Audit Anchor`。另行创建受 ACL 保护的 `appsettings.Production.json`，不要把示例值用于生产：

```json
{
  "AuditAnchorReceiver": {
    "ClientToken": "<随机且仅该 BRMES 实例使用的 bearer token>",
    "CertificateThumbprint": "<LocalMachine\\My 中回执签名证书的 SHA-1 或 SHA-256 thumbprint>",
    "DatabasePath": "C:\\ProgramData\\BRMES-AuditAnchor\\audit-anchor.db",
    "RequireHttps": true
  }
}
```

将回执签名证书导入 `Cert:\LocalMachine\My`，确认服务身份可以读取其 RSA 私钥。签名 RSA 密钥至少 2048 位，证书必须在有效期内。建立数据目录并只授予服务身份及受控管理员访问权限。示例服务注册命令（以实际 exe 路径、账户和目录替换）：

```powershell
New-Item -ItemType Directory -Force C:\ProgramData\BRMES-AuditAnchor | Out-Null
New-Service -Name BRMESAuditAnchor `
  -BinaryPathName '"C:\Program Files\BRMES Audit Anchor\RecipesManage.AuditAnchor.Server.exe"' `
  -DisplayName 'BRMES External Audit Anchor' -StartupType Automatic
```

服务以 `Production` 环境运行，确保发布目录内存在受保护的 `appsettings.Production.json`。将 TLS 证书配置给 Kestrel，并将监听地址绑定到受控网卡/端口，例如使用 Windows 服务环境变量：

```text
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=https://+:5443
ASPNETCORE_Kestrel__Certificates__Default__Path=C:\ProgramData\BRMES-AuditAnchor\tls.pfx
ASPNETCORE_Kestrel__Certificates__Default__Password=<由受保护的服务配置提供>
```

`RequireHttps=true` 时应用会检查 ASP.NET 请求的 `IsHttps`。当前服务没有配置 forwarded headers；不要在未验证可信代理头处理的情况下让反向代理以 HTTP 转发到应用。推荐 Kestrel 端到端 TLS，或先实现并验证只信任指定代理的 forwarded headers。防火墙只放行 BRMES 主机到接收端 TLS 端口的流量。

启动服务后检查 `https://<receiver>:5443/health` 返回 `status=ok`、`service=audit-anchor`、`keyId` 与 `pid`。该健康端点不要求 bearer token；不要暴露到公共网络。

## BRMES 配置与启用

为该 BRMES 安装生成并永久保存一个稳定的 `SourceId` GUID。为每个安装使用不同 ID；不要因升级、服务重启或数据库维护而更换。将接收端签名证书的**公开证书** PEM 复制到 BRMES 受保护目录，并通过运维流程核对其指纹。

在 BRMES 生产配置中设置：

```json
{
  "AuditAnchor": {
    "Enabled": true,
    "AtUtc": "02:45",
    "RetryMinutes": 5,
    "SourceId": "<该安装固定的 GUID>",
    "Endpoint": "https://<receiver>:5443/",
    "ClientToken": "<与接收端相同的 bearer token>",
    "VerificationCertificatePath": "C:\\ProgramData\\BRMES\\certs\\audit-anchor-receiver.pem"
  }
}
```

确保 BRMES 服务身份可读配置、token 和 PEM 文件。非 Development 环境拒绝 HTTP endpoint，token 少于 32 UTF-8 字节或 PEM 缺失时也不会启用。配置完成后重启 BRMES 服务；管理员可在「用户与备份」页查看状态并点击「立即锚定」。每日任务按 UTC 运行，失败会保留错误并按 `RetryMinutes` 重试；手动锚定与自动锚定互斥。

BRMES 客户端在提交前以 PEM 公钥验证回执签名、来源、快照 ID、清单摘要和 KeyId。首次接收和配置生效后应立即手动锚定一次，确认状态页显示成功时间、序号、回执摘要和签名 KeyId。

## 轮换、备份与恢复

- **Token 轮换**：在维护窗口同时更新接收端与 BRMES 两侧的 token，限制旧 token 的保留时间，重启两端服务并立即手动锚定验证。当前接收端只配置一个 token，没有按来源分别授权或双 token 重叠轮换功能。
- **签名证书轮换**：当前 BRMES 只信任一个 PEM/KeyId，不支持自动多证书重叠验证。先受控安装新签名证书并保留旧公钥，再协调切换接收端 thumbprint 与 BRMES PEM/服务重启；验证新回执后，按保留期限保存旧公钥以离线验证历史回执。不得删除旧私钥/公钥材料，直到历史证据保留策略允许。
- **接收端数据库保护**：对 `DatabasePath` 做与 BRMES 主机分离的受控备份，记录备份时最新的 `source_id`、`sequence` 和 `receipt_hash`。备份不得由 BRMES 服务身份改写。SQLite WAL 运行时不要直接复制主 `.db` 文件；使用 SQLite Backup API，或先停服务并确认 checkpoint 后备份。
- **恢复前校验**：还原旧 ledger 可能回退序号/链头。恢复后重新开放服务前，必须用独立保存的最新签名回执核对每个来源的序号与摘要链；若无法证明恢复库包含最后已确认回执，应暂停接收、保全现状并按事件处理，不要直接继续签发。
- **状态留存**：BRMES 页面中的最近状态是进程内状态，重启后会重置；已签回执仍保存在接收端 ledger。重启后可手工提交新快照建立最新状态。

## 装机验收与故障处理

1. 从 BRMES 主机请求接收端 `/health`，验证 DNS、端口、TLS 链及接收端 `keyId`。
2. 在 BRMES 管理员页面确认启用、UTC 排程、endpoint 和签名 KeyId 与交付记录一致。
3. 点击「立即锚定」，确认接口成功、回执序号递增，状态页显示成功时间与摘要。
4. 在接收端查询 `GET /v1/anchors/{sourceId}?afterSequence=0&take=500`，携带 bearer token 并通过 HTTPS，记录响应的 `latestSequence`。后续页使用上一页最后一个序号作为 `afterSequence`，并固定传入 `throughSequence=<latestSequence>`；持续读取到该序号或返回空页，将所有 JSON 响应保存在同一目录。
5. 使用离线验证工具验证清单摘要、签名、证书轮换和完整序号链。生产 ledger 不做破坏性篡改测试。
6. 在测试环境验证网络断开、错误 token、过期/错误 PEM、HTTP endpoint、重放快照 ID 冲突等失败路径；生产审计数据必须保持不变，管理员页应能看到失败原因并支持重试。

## 离线验证

从受控发布机发布验证工具：

```powershell
dotnet publish src\RecipesManage.AuditAnchor.Verify\RecipesManage.AuditAnchor.Verify.csproj `
  -c Release -r win-x64 --self-contained true -o .\artifacts\audit-anchor-verify
```

将回执页 JSON 放在一个目录内，将当前及历史回执签名证书的公开 PEM 文件放在另一个目录内，然后运行：

```powershell
.\RecipesManage.AuditAnchor.Verify.exe `
  --input .\receipt-pages `
  --certificates .\public-certificates `
  --expected-head 123:<独立留存的64位回执摘要>
```

`--input` 也可直接指定单页 JSON（适用于完整链可由该页返回的场景）；大于单页上限时必须提供从序号 1 开始的全部分页文件。验证器要求各页来源和 `latestSequence` 一致，拒绝重复/缺失序号，逐条核对规范化 manifest 摘要、RSA-PSS 签名和前序回执哈希，并允许通过 `--certificates` 目录验证跨密钥轮换的回执。为限制离线导入资源，输入最多 10,000 页、总计 128 MiB；每个回执页最多 16 MiB，每张 PEM 最多 1 MiB。命令返回非零退出码表示验证失败。

`latestSequence` 是导出响应中的未签名分页元数据。没有 `--expected-head` 时，工具只能确认数据与导出页自报的链头一致，无法检测接收端 ledger 本身被回滚；应将序号和摘要通过独立渠道留存，并在核验时作为 `--expected-head` 提供。每日报告/回执链的异机保留与第三方监督仍属于现场运维责任。
