using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RecipesManage.Domain.Recipes;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// 审批链从"代码里写死的 Author → Supervisor → Quality"变成可配置的数据。
///
/// 为什么：级数、每级要谁签、签名含义三件事全在枚举与两处 if/else 里，
/// 换一个需要四道签核或不要工艺主管的产品线就得改代码发版。
///
/// 这一步做了什么：
/// - 新表 approval_chains 存命名链（节点定义整体存 JSON，与 phase_templates.ParametersJson 同法），
///   并种一条与现行行为逐字相同的 standard 作为默认链。
/// - master_recipes 多一个可空的 ApprovalChainCode（null = 走默认链）。
/// - approval_records **去掉 Level**，换成提交时冻结下来的副本：
///   Seq（链上顺序）/ Node（稳定标识，只为回填与缺省文案）/ Title / RequiredRole /
///   MeaningApproved / MeaningRejected。展示用的 Meaning 由这几列现推，不落库。
///   唯一索引随之从 (RecipeVersionId, Level) 换成 (RecipeVersionId, Seq)。
///
/// 历史行按 Level 原样翻译，包括旧的签名含义文案（那三档文案在这里以字面量重复一遍，
/// 而不是引用代码——迁移是历史，不能让后来的文案改动改写已经发生过的事）。
/// 含义文案与 <see cref="ApprovalChain.Standard"/> 有意不一致的地方只有未决提示：
/// 旧文案是"待工艺主管签署：确认工艺路径可执行。"，新实现统一成"待工艺主管签署。"，
/// 它只是界面提示，不是任何人签过的东西。
///
/// 失败模式（有意保留）：Level 靠 DROP COLUMN 删除，要求 SQLite ≥ 3.35；
/// 若历史上同一版本出现过重复 Level 行，回填后的 CREATE UNIQUE INDEX 会抛，
/// 与补索引那次的取舍一致——显式失败比继续静默坏着好。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260923120000_ApprovalChains")]
public partial class ApprovalChains : Migration
{
    private static readonly Guid StandardChainId = new("0d7a1f43-6c58-4f2b-9b1e-5a2c7d34e8a1");

    /// <summary>与 ApprovalChain.Standard 同一份定义，免得种下去的链与代码里的缺省链各说各话。</summary>
    private static readonly string StepsJson = ApprovalChainConfig.Write(ApprovalChain.Standard.Steps);

    private const string SeedAt = "2026-01-01 00:00:00+00:00";

    // 旧 ElectronicSignature.Meaning 的三档文案，回填历史行原样保留。
    private const string AuthorApproved =
        "我作为工艺工程师确认本版本 Procedure / Steps 与 Parameters / Setpoints 准确，提交多级审核。";
    private const string AuthorRejected = "工艺工程师提交审核。";
    private const string SupervisorApproved = "我作为工艺主管确认工艺路径可执行，批准进入质量审核。";
    private const string SupervisorRejected = "我作为工艺主管驳回：工艺路径不可执行或需要返工。";
    private const string QualityApproved = "我作为质量审核人确认参数窗口可接受，批准本版本作为生效主配方。";
    private const string QualityRejected = "我作为质量审核人驳回：参数窗口不可接受或需要返工。";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var t = DualColumn.For(migrationBuilder);

        migrationBuilder.CreateTable(
            name: "approval_chains",
            columns: table => new
            {
                Id = table.Column<Guid>(type: t.Guid, nullable: false),
                Code = table.Column<string>(type: t.Text, nullable: false),
                Name = table.Column<string>(type: t.Text, nullable: false),
                StepsJson = table.Column<string>(type: t.Json, nullable: false),
                IsDefault = table.Column<bool>(type: t.Bool, nullable: false, defaultValue: false),
                Enabled = table.Column<bool>(type: t.Bool, nullable: false, defaultValue: true),
                CreatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_approval_chains", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_approval_chains_Code",
            table: "approval_chains",
            column: "Code",
            unique: true);

        // 种子就是代码里的缺省链，二者只有一份定义，不会各说各话。
        // 用裸 SQL 而不是 InsertData：手写迁移没有 .Designer.cs，EF 无法把表名映射回实体，
        // InsertData 会在生成 SQL 时直接抛"no entity type mapped to the table"。
        // TRUE/FALSE 与带时区的 ISO 字面量两家提供器都认。
        migrationBuilder.Sql(
            $"""
             INSERT INTO approval_chains
                 ("Id", "Code", "Name", "StepsJson", "IsDefault", "Enabled", "CreatedAt", "UpdatedAt")
             SELECT '{StandardChainId}', 'standard', '标准三级',
                    '{StepsJson.Replace("'", "''")}', TRUE, TRUE, '{SeedAt}', NULL
             WHERE NOT EXISTS (SELECT 1 FROM approval_chains WHERE "Code" = 'standard');
             """);

        migrationBuilder.AddColumn<string>(
            name: "ApprovalChainCode",
            table: "master_recipes",
            type: t.Text,
            nullable: true);

        // 索引必须先删：SQLite 不允许删除仍被索引引用的列。
        migrationBuilder.DropIndex(
            name: "IX_approval_records_RecipeVersionId_Level",
            table: "approval_records");

        migrationBuilder.AddColumn<int>(
            name: "Seq", table: "approval_records", type: t.Int, nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(
            name: "Node", table: "approval_records", type: t.Int, nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(
            name: "RequiredRole", table: "approval_records", type: t.Int, nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>(
            name: "Title", table: "approval_records", type: t.Text, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "MeaningApproved", table: "approval_records", type: t.Text, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "MeaningRejected", table: "approval_records", type: t.Text, nullable: false, defaultValue: "");

        // Level: Author=0 / Supervisor=1 / Quality=2 → Seq 同序。
        // 名称与含义用字面量，不引用领域常量：迁移是历史，不能让后来的改名改写已经发生过的事。
        // 但 Node / RequiredRole 按枚举取数——UserRole 的 0 是 Admin，手写 0/1/2 会把每条历史
        // 审批行都指向错的角色（实测：主管的待签节点变成要求工艺工程师，于是谁都签不了）。
        var submissionNode = (int)ApprovalNode.Submission;
        var supervisorNode = (int)ApprovalNode.Supervisor;
        var qualityNode = (int)ApprovalNode.Quality;
        var submissionRole = (int)ApprovalNode.Submission.ToRequiredRole();
        var supervisorRole = (int)ApprovalNode.Supervisor.ToRequiredRole();
        var qualityRole = (int)ApprovalNode.Quality.ToRequiredRole();

        migrationBuilder.Sql(
            $"""
             UPDATE approval_records SET
                 "Seq" = 0, "Node" = {submissionNode}, "RequiredRole" = {submissionRole},
                 "Title" = '提交人',
                 "MeaningApproved" = '{AuthorApproved}', "MeaningRejected" = '{AuthorRejected}'
             WHERE "Level" = 0;
             """);
        migrationBuilder.Sql(
            $"""
             UPDATE approval_records SET
                 "Seq" = 1, "Node" = {supervisorNode}, "RequiredRole" = {supervisorRole},
                 "Title" = '工艺主管',
                 "MeaningApproved" = '{SupervisorApproved}', "MeaningRejected" = '{SupervisorRejected}'
             WHERE "Level" = 1;
             """);
        migrationBuilder.Sql(
            $"""
             UPDATE approval_records SET
                 "Seq" = 2, "Node" = {qualityNode}, "RequiredRole" = {qualityRole},
                 "Title" = '质量审核',
                 "MeaningApproved" = '{QualityApproved}', "MeaningRejected" = '{QualityRejected}'
             WHERE "Level" = 2;
             """);

        migrationBuilder.Sql("""UPDATE approval_records SET "Node" = 0 WHERE "Level" NOT IN (0, 1, 2);""");

        migrationBuilder.Sql("""ALTER TABLE approval_records DROP COLUMN "Level";""");

        migrationBuilder.CreateIndex(
            name: "IX_approval_records_RecipeVersionId_Seq",
            table: "approval_records",
            columns: new[] { "RecipeVersionId", "Seq" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        var t = DualColumn.For(migrationBuilder);

        migrationBuilder.DropIndex(
            name: "IX_approval_records_RecipeVersionId_Seq",
            table: "approval_records");

        migrationBuilder.AddColumn<int>(
            name: "Level", table: "approval_records", type: t.Int, nullable: false, defaultValue: 0);
        migrationBuilder.Sql("""UPDATE approval_records SET "Level" = "Seq" WHERE "Seq" IN (0, 1, 2);""");
        // 回退到写死的三级：链更长时多余节点没有对应的 Level，折成质量级保住数据不丢。
        migrationBuilder.Sql("""UPDATE approval_records SET "Level" = 2 WHERE "Seq" > 2;""");

        // SQLite 提供器不支持 DropColumn（Up 与 Down 都一样），只能自己发 ALTER。
        foreach (var column in new[]
                 {
                     "Seq", "Node", "RequiredRole", "Title", "MeaningApproved", "MeaningRejected"
                 })
        {
            migrationBuilder.Sql($"""ALTER TABLE approval_records DROP COLUMN "{column}";""");
        }

        migrationBuilder.CreateIndex(
            name: "IX_approval_records_RecipeVersionId_Level",
            table: "approval_records",
            columns: new[] { "RecipeVersionId", "Level" },
            unique: true);

        migrationBuilder.Sql("""ALTER TABLE master_recipes DROP COLUMN "ApprovalChainCode";""");
        migrationBuilder.DropTable(name: "approval_chains");
    }
}
