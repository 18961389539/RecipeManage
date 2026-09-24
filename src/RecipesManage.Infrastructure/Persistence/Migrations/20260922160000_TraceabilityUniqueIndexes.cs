using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// 给两条"追溯读取依赖"的键补唯一索引。
///
/// 为什么是这两条：
/// - recipe_steps(RecipeVersionId, Code)：Code 是控制配方快照 ↔ 工步执行记录 ↔ 快照漂移比对
///   之间的连接键，下游用 <c>ToDictionary(s =&gt; s.Code)</c> 取工步。同版本重码不会立刻报错，
///   而是让该配方的<strong>所有</strong>批次详情与电子批记录永久 500。
/// - approval_records(RecipeVersionId, Level)：取待审节点用的是 <c>Single(Pending)</c>，
///   一个版本同一级多一条就整版不可审。Submit / ReopenRejected 都会先 Clear 再开，所以唯一是既有语义。
///
/// 失败模式（有意保留）：若目标库已存在重复行，本迁移会在 CREATE UNIQUE INDEX 处抛
/// "UNIQUE constraint failed: …"，启动失败。这不是回归——那种数据下上述读取路径本来就已经在 500，
/// 让它在启动时显式失败比让它继续静默坏着好。先人工并掉重复行再启动。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260922160000_TraceabilityUniqueIndexes")]
public partial class TraceabilityUniqueIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // EF 的外键索引约定：已有索引的最左前缀覆盖外键列时，它不再单独建 FK 索引。
        // 所以加复合唯一索引会把这两条单列索引从模型里"挤掉"——快照必须同步删，
        // 库里也要真的删掉，否则下次比对就是 PendingModelChangesWarning（本项目实测过）。
        migrationBuilder.DropIndex(
            name: "IX_recipe_steps_RecipeVersionId",
            table: "recipe_steps");

        migrationBuilder.DropIndex(
            name: "IX_approval_records_RecipeVersionId",
            table: "approval_records");

        migrationBuilder.CreateIndex(
            name: "IX_recipe_steps_RecipeVersionId_Code",
            table: "recipe_steps",
            columns: new[] { "RecipeVersionId", "Code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_approval_records_RecipeVersionId_Level",
            table: "approval_records",
            columns: new[] { "RecipeVersionId", "Level" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_recipe_steps_RecipeVersionId_Code",
            table: "recipe_steps");

        migrationBuilder.DropIndex(
            name: "IX_approval_records_RecipeVersionId_Level",
            table: "approval_records");

        migrationBuilder.CreateIndex(
            name: "IX_recipe_steps_RecipeVersionId",
            table: "recipe_steps",
            column: "RecipeVersionId");

        migrationBuilder.CreateIndex(
            name: "IX_approval_records_RecipeVersionId",
            table: "approval_records",
            column: "RecipeVersionId");
    }
}
