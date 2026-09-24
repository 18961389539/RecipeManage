using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// 工艺参数带上显式语义与实测点绑定。
///
/// 为什么：归档取哪个实测点、哪个参数是工艺时长，原先靠名称里的中文关键词猜
/// （"温度/时长/斜率/压"）。换一个不带温度的工艺就会静默算错——比不报错更糟。
/// 声明后由 <c>Semantic</c> / <c>MeasuredTag</c> 说了算，关键词推断退化为历史数据的回退路径。
///
/// 兼容性：两列都可空/有默认值，历史行不回填（回填等于替用户猜语义，正是本次要消除的行为）。
/// 快照侧字段用 JsonIgnore WhenWritingDefault / WhenWritingNull，已密封批次的完整性哈希不变。
/// 相模板参数存在 phase_templates.ParametersJson 里，不需要迁移。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260922180000_ParameterSemantics")]
public partial class ParameterSemanticsColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var t = DualColumn.For(migrationBuilder);

        migrationBuilder.AddColumn<int>(
            name: "Semantic",
            table: "recipe_parameters",
            type: t.Int,
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "MeasuredTag",
            table: "recipe_parameters",
            type: t.Text,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Semantic", table: "recipe_parameters");
        migrationBuilder.DropColumn(name: "MeasuredTag", table: "recipe_parameters");
    }
}
