using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// 调度意图的唯一键从 (批次, 种类) 放宽到 (批次, 种类, 工步)：并行车道上两个工步同时等确认时，
/// 旧键让第二条确认覆盖第一条。既有行在旧键下唯一，在新键下必然也唯一，无需搬数据。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261002120000_SchedulerIntentPerStep")]
public partial class SchedulerIntentPerStep : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_scheduler_intents_BatchId_Kind",
            table: "scheduler_intents");

        migrationBuilder.CreateIndex(
            name: "IX_scheduler_intents_BatchId_Kind_StepId",
            table: "scheduler_intents",
            columns: ["BatchId", "Kind", "StepId"],
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_scheduler_intents_BatchId_Kind_StepId",
            table: "scheduler_intents");

        migrationBuilder.CreateIndex(
            name: "IX_scheduler_intents_BatchId_Kind",
            table: "scheduler_intents",
            columns: ["BatchId", "Kind"],
            unique: true);
    }
}
