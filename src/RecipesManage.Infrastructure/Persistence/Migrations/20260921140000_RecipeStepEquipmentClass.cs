using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// 工步持久化单元规程声明的设备类，设计器不再只靠程序号推断，开批可按类优先选设备。
/// 空值兼容已批准配方；快照字段 JsonIgnore WhenWritingNull，不改旧批哈希。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260921140000_RecipeStepEquipmentClass")]
public partial class RecipeStepEquipmentClass : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var t = DualColumn.For(migrationBuilder);
        migrationBuilder.AddColumn<string>(
            name: "EquipmentClassCode",
            table: "recipe_steps",
            type: t.Text,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "EquipmentClassCode", table: "recipe_steps");
    }
}
