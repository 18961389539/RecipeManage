using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// 电子签名从"审计日志里的一行拼接文本"变成一张结构化表 signature_records。
///
/// 为什么：批次 / 化验的签名只存在 audit_logs.Detail = "含义 + 备注" 里，批记录展示时用**当前代码**里的
/// 含义表反查再剥前缀——改一次措辞，历史记录展示的就不再是当时签名人确认的那句话。
/// 新表在签署时把含义原文冻结成列（Meaning）并与备注（Detail）分开。
///
/// 回填（历史行一条不丢、一个字不改）：
/// 1. audit_logs 里 Action 以 .esign 结尾、对象是批次或化验样品的行，按"Detail 以已知含义原文开头"拆成
///    Meaning + Detail。这十句原文在这里以字面量重复一遍，而不是引用代码——
///    迁移是历史，不能让后来的措辞改动改写已经发生过的签名。
/// 2. 其余（措辞与现行不同的更早文本）整段 Detail 原样作为 Meaning、Detail 置空：宁可含义里夹带备注，
///    也不替签名人编一句他没签过的话。
/// 回填行沿用 audit_logs 行的 Id，所以重复执行不会重复插入，也能反查到对应的审计行。
///
/// 配方侧的签名不在此表：审核节点的含义早已冻结在 approval_records（见 ApprovalChains 迁移）。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261002130000_SignatureRecords")]
public partial class SignatureRecords : Migration
{
    private static readonly (string Action, string Meaning)[] KnownMeanings =
    [
        ("batch.start.esign", "我作为操作员确认控制配方快照完整有效，启动本批四步握手，禁止盲写。"),
        ("batch.retry.esign", "我作为操作员确认故障已排除，从当前工步重新排队并恢复握手。"),
        ("batch.abort.esign", "我作为操作员确认中止本批，停止写参并释放设备占用。"),
        ("batch.hold.esign", "我作为操作员确认请求保持：写 Host_Hold，等待 PLC_Held，禁止盲写下一步。"),
        ("batch.resume.esign", "我作为操作员确认解除保持，从当前工步继续四步握手。"),
        ("batch.skip.esign", "我作为主管确认跳过当前工步：仅在未写参的就绪/等待/确认相位，禁止跨阶段盲写。"),
        ("batch.confirm.esign", "我作为操作员确认本工步人工确认点已核对，允许继续且本工步不写 PLC。"),
        ("batch.release.esign", "我作为质量审核人对照归档质检与四步握手，批准本批放行。"),
        ("batch.reject.esign", "我作为质量审核人对照归档质检与四步握手，拒收本批。"),
        ("lab.sample.dispose.esign", "我作为质量审核人对照规格判定本样品。"),
    ];

    private const string Columns =
        "\"Id\", \"UserId\", \"SignerName\", \"Action\", \"EntityType\", \"EntityId\", \"Meaning\", \"Detail\", \"SignedAt\", \"CreatedAt\", \"UpdatedAt\"";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var t = DualColumn.For(migrationBuilder);

        migrationBuilder.CreateTable(
            name: "signature_records",
            columns: table => new
            {
                Id = table.Column<Guid>(type: t.Guid, nullable: false),
                UserId = table.Column<Guid>(type: t.Guid, nullable: true),
                SignerName = table.Column<string>(type: t.Text, nullable: false),
                Action = table.Column<string>(type: t.Text, nullable: false),
                EntityType = table.Column<string>(type: t.Text, nullable: false),
                EntityId = table.Column<string>(type: t.Text, nullable: false),
                Meaning = table.Column<string>(type: t.Text, nullable: false),
                Detail = table.Column<string>(type: t.Text, nullable: true),
                SignedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_signature_records", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_signature_records_EntityType_EntityId_SignedAt",
            table: "signature_records",
            columns: ["EntityType", "EntityId", "SignedAt"]);

        // 手写迁移没有 .Designer.cs，InsertData 无法把表名映射回实体，所以用裸 SQL（与 ApprovalChains 同因）。
        foreach (var (action, meaning) in KnownMeanings)
        {
            var literal = meaning.Replace("'", "''");
            migrationBuilder.Sql(
                $"""
                 INSERT INTO signature_records ({Columns})
                 SELECT a."Id", a."UserId", a."UserName", a."Action", a."EntityType", a."EntityId",
                        '{literal}',
                        NULLIF(TRIM(SUBSTR(a."Detail", {meaning.Length + 1})), ''),
                        a."At", a."CreatedAt", NULL
                 FROM audit_logs a
                 WHERE a."Action" = '{action}'
                   AND a."EntityType" IN ('ProductionBatch', 'LabSample')
                   AND a."Detail" IS NOT NULL
                   AND SUBSTR(a."Detail", 1, {meaning.Length}) = '{literal}'
                   AND NOT EXISTS (SELECT 1 FROM signature_records s WHERE s."Id" = a."Id");
                 """);
        }

        migrationBuilder.Sql(
            $"""
             INSERT INTO signature_records ({Columns})
             SELECT a."Id", a."UserId", a."UserName", a."Action", a."EntityType", a."EntityId",
                    COALESCE(NULLIF(TRIM(a."Detail"), ''), '电子签名。'),
                    NULL, a."At", a."CreatedAt", NULL
             FROM audit_logs a
             WHERE a."Action" LIKE '%.esign'
               AND a."EntityType" IN ('ProductionBatch', 'LabSample')
               AND NOT EXISTS (SELECT 1 FROM signature_records s WHERE s."Id" = a."Id");
             """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // 审计日志里原本就有对应行，回退不丢信息。
        migrationBuilder.DropTable(name: "signature_records");
    }
}
