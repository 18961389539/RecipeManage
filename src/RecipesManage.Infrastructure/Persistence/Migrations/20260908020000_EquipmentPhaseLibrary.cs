using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260908020000_EquipmentPhaseLibrary")]
public partial class EquipmentPhaseLibrary : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var t = DualColumn.For(migrationBuilder);
        migrationBuilder.AddColumn<string>(
            name: "EquipmentClassCode",
            table: "equipment",
            type: t.Text,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "equipment_classes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: t.Guid, nullable: false),
                Code = table.Column<string>(type: t.Text, nullable: false),
                Name = table.Column<string>(type: t.Text, nullable: false),
                Description = table.Column<string>(type: t.Text, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_equipment_classes", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_equipment_classes_Code",
            table: "equipment_classes",
            column: "Code",
            unique: true);

        migrationBuilder.CreateTable(
            name: "phase_templates",
            columns: table => new
            {
                Id = table.Column<Guid>(type: t.Guid, nullable: false),
                EquipmentClassId = table.Column<Guid>(type: t.Guid, nullable: false),
                Code = table.Column<string>(type: t.Text, nullable: false),
                Name = table.Column<string>(type: t.Text, nullable: false),
                StepType = table.Column<int>(type: t.Int, nullable: false),
                Operation = table.Column<string>(type: t.Text, nullable: false),
                WatchdogSeconds = table.Column<int>(type: t.Int, nullable: false),
                ParametersJson = table.Column<string>(type: t.Json, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_phase_templates", x => x.Id);
                table.ForeignKey(
                    name: "FK_phase_templates_equipment_classes_EquipmentClassId",
                    column: x => x.EquipmentClassId,
                    principalTable: "equipment_classes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_phase_templates_Code",
            table: "phase_templates",
            column: "Code",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_phase_templates_EquipmentClassId",
            table: "phase_templates",
            column: "EquipmentClassId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "phase_templates");
        migrationBuilder.DropTable(name: "equipment_classes");
        migrationBuilder.DropColumn(name: "EquipmentClassCode", table: "equipment");
    }
}
