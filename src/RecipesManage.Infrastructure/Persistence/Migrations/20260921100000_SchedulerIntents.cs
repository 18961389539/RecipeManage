using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// 调度保持 / 跳步 / 人工确认从进程内字典落到 scheduler_intents，
/// 引擎重启后仍能恢复已电子签名的操作员指令。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260921100000_SchedulerIntents")]
public partial class SchedulerIntents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var t = DualColumn.For(migrationBuilder);

        migrationBuilder.CreateTable(
            name: "scheduler_intents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: t.Guid, nullable: false),
                BatchId = table.Column<Guid>(type: t.Guid, nullable: false),
                Kind = table.Column<string>(type: t.Text, nullable: false),
                Reason = table.Column<string>(type: t.Text, nullable: false),
                StepId = table.Column<Guid>(type: t.Guid, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_scheduler_intents", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_scheduler_intents_BatchId",
            table: "scheduler_intents",
            column: "BatchId");

        migrationBuilder.CreateIndex(
            name: "IX_scheduler_intents_BatchId_Kind",
            table: "scheduler_intents",
            columns: ["BatchId", "Kind"],
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "scheduler_intents");
    }
}
