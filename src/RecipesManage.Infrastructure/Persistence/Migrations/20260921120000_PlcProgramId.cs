using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// 工步 / 相模板增加 PlcProgramId：下发 Step_Type 与 StepType 枚举解耦，
/// 空值回退为枚举整型，兼容已冻结快照与已批准配方。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260921120000_PlcProgramId")]
public partial class PlcProgramId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var t = DualColumn.For(migrationBuilder);

        migrationBuilder.AddColumn<int>(
            name: "PlcProgramId",
            table: "recipe_steps",
            type: t.Int,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "PlcProgramId",
            table: "phase_templates",
            type: t.Int,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PlcProgramId", table: "recipe_steps");
        migrationBuilder.DropColumn(name: "PlcProgramId", table: "phase_templates");
    }
}
