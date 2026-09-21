using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260908003000_MaterialGenealogy")]
public partial class MaterialGenealogy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var t = DualColumn.For(migrationBuilder);
        migrationBuilder.CreateTable(
            name: "material_lots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: t.Guid, nullable: false),
                LotNumber = table.Column<string>(type: t.Text, nullable: false),
                MaterialCode = table.Column<string>(type: t.Text, nullable: false),
                MaterialName = table.Column<string>(type: t.Text, nullable: false),
                ParentLotId = table.Column<Guid>(type: t.Guid, nullable: true),
                Source = table.Column<int>(type: t.Int, nullable: false),
                Status = table.Column<int>(type: t.Int, nullable: false),
                Quantity = table.Column<double>(type: t.Real, nullable: true),
                Uom = table.Column<string>(type: t.Text, nullable: true),
                ProducedBatchId = table.Column<Guid>(type: t.Guid, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_material_lots", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_material_lots_LotNumber",
            table: "material_lots",
            column: "LotNumber",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_material_lots_ParentLotId",
            table: "material_lots",
            column: "ParentLotId");

        migrationBuilder.CreateTable(
            name: "batch_material_uses",
            columns: table => new
            {
                Id = table.Column<Guid>(type: t.Guid, nullable: false),
                BatchId = table.Column<Guid>(type: t.Guid, nullable: false),
                MaterialLotId = table.Column<Guid>(type: t.Guid, nullable: false),
                Role = table.Column<int>(type: t.Int, nullable: false),
                Quantity = table.Column<double>(type: t.Real, nullable: true),
                StepId = table.Column<Guid>(type: t.Guid, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_batch_material_uses", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_batch_material_uses_BatchId_MaterialLotId_Role",
            table: "batch_material_uses",
            columns: new[] { "BatchId", "MaterialLotId", "Role" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "lab_samples",
            columns: table => new
            {
                Id = table.Column<Guid>(type: t.Guid, nullable: false),
                SampleCode = table.Column<string>(type: t.Text, nullable: false),
                BatchId = table.Column<Guid>(type: t.Guid, nullable: false),
                MaterialLotId = table.Column<Guid>(type: t.Guid, nullable: true),
                ParentSampleId = table.Column<Guid>(type: t.Guid, nullable: true),
                StepId = table.Column<Guid>(type: t.Guid, nullable: true),
                SampleType = table.Column<int>(type: t.Int, nullable: false),
                Disposition = table.Column<int>(type: t.Int, nullable: false),
                ResultsJson = table.Column<string>(type: t.Json, nullable: true),
                TakenBy = table.Column<string>(type: t.Text, nullable: false),
                TakenAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                DispositionBy = table.Column<string>(type: t.Text, nullable: true),
                DisposedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: true),
                Comment = table.Column<string>(type: t.Text, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_lab_samples", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_lab_samples_SampleCode",
            table: "lab_samples",
            column: "SampleCode",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_lab_samples_BatchId",
            table: "lab_samples",
            column: "BatchId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "lab_samples");
        migrationBuilder.DropTable(name: "batch_material_uses");
        migrationBuilder.DropTable(name: "material_lots");
    }
}
