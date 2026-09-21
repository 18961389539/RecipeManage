using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260907153000_BatchDisposition")]
public partial class BatchDisposition : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var t = DualColumn.For(migrationBuilder);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ReleasedAt",
            table: "production_batches",
            type: t.Ts,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ReleasedBy",
            table: "production_batches",
            type: t.Text,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ReleaseComment",
            table: "production_batches",
            type: t.Text,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ReleasedAt", table: "production_batches");
        migrationBuilder.DropColumn(name: "ReleasedBy", table: "production_batches");
        migrationBuilder.DropColumn(name: "ReleaseComment", table: "production_batches");
    }
}
