using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// 1) 车道相位从 production_batches 拆出为独立的 batch_lanes（每条设备一行），
///    消除多 lane 每 tick 争夺同一行、以及与 HTTP 控制写互相覆盖的隐患。
/// 2) 新增 equipment_leases：设备占用排他交给数据库唯一索引。
/// 3) production_batches / batch_step_executions 增加 ConcurrencyStamp 乐观并发令牌。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260920010000_LaneStateAndLeases")]
public partial class LaneStateAndLeases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var t = DualColumn.For(migrationBuilder);
        var pg = DualColumn.IsNpgsql(migrationBuilder);

        migrationBuilder.AddColumn<Guid>(
            name: "ConcurrencyStamp",
            table: "batch_step_executions",
            type: t.Guid,
            nullable: false,
            defaultValue: Guid.Empty);

        migrationBuilder.AddColumn<Guid>(
            name: "ConcurrencyStamp",
            table: "production_batches",
            type: t.Guid,
            nullable: false,
            defaultValue: Guid.Empty);

        // 存量行回填互不相同的令牌，避免所有历史批次共用同一个 Guid.Empty。
        Backfill(migrationBuilder, pg, "batch_step_executions");
        Backfill(migrationBuilder, pg, "production_batches");

        migrationBuilder.CreateTable(
            name: "batch_lanes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: t.Guid, nullable: false),
                BatchId = table.Column<Guid>(type: t.Guid, nullable: false),
                EquipmentId = table.Column<Guid>(type: t.Guid, nullable: false),
                EquipmentCode = table.Column<string>(type: t.Text, nullable: false),
                UnitProcedure = table.Column<string>(type: t.Text, nullable: false),
                StepId = table.Column<Guid>(type: t.Guid, nullable: true),
                StepCode = table.Column<string>(type: t.Text, nullable: false),
                Phase = table.Column<string>(type: t.Text, nullable: false),
                Outcome = table.Column<string>(type: t.Text, nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: t.Guid, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_batch_lanes", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_batch_lanes_BatchId",
            table: "batch_lanes",
            column: "BatchId");

        migrationBuilder.CreateIndex(
            name: "IX_batch_lanes_BatchId_EquipmentId",
            table: "batch_lanes",
            columns: ["BatchId", "EquipmentId"],
            unique: true);

        migrationBuilder.CreateTable(
            name: "equipment_leases",
            columns: table => new
            {
                Id = table.Column<Guid>(type: t.Guid, nullable: false),
                EquipmentId = table.Column<Guid>(type: t.Guid, nullable: false),
                EquipmentCode = table.Column<string>(type: t.Text, nullable: false),
                BatchId = table.Column<Guid>(type: t.Guid, nullable: false),
                BatchNo = table.Column<string>(type: t.Text, nullable: false),
                LeasedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_equipment_leases", x => x.Id);
            });

        // 排他占用：同一台设备最多一行租约，并发 START 时后到者直接命中唯一索引冲突。
        migrationBuilder.CreateIndex(
            name: "IX_equipment_leases_EquipmentId",
            table: "equipment_leases",
            column: "EquipmentId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_equipment_leases_BatchId",
            table: "equipment_leases",
            column: "BatchId");

        migrationBuilder.CreateTable(
            name: "applied_data_fixes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: t.Guid, nullable: false),
                Key = table.Column<string>(type: t.Text, nullable: false),
                AppliedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                Note = table.Column<string>(type: t.Text, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_applied_data_fixes", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_applied_data_fixes_Key",
            table: "applied_data_fixes",
            column: "Key",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "applied_data_fixes");
        migrationBuilder.DropTable(name: "equipment_leases");
        migrationBuilder.DropTable(name: "batch_lanes");
        migrationBuilder.DropColumn(name: "ConcurrencyStamp", table: "production_batches");
        migrationBuilder.DropColumn(name: "ConcurrencyStamp", table: "batch_step_executions");
    }

    private static void Backfill(MigrationBuilder migrationBuilder, bool pg, string table)
    {
        migrationBuilder.Sql(pg
            ? $"UPDATE \"{table}\" SET \"ConcurrencyStamp\" = gen_random_uuid();"
            : $"UPDATE \"{table}\" SET \"ConcurrencyStamp\" = " +
              "lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)),2) || " +
              "'-' || substr('89ab', abs(random()) % 4 + 1, 1) || substr(hex(randomblob(2)),2) || '-' || hex(randomblob(6)));");
    }
}
