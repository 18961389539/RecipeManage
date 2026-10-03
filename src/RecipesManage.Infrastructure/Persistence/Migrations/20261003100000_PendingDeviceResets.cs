using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// 中止后"这台设备还欠一次握手位复位"的持久标记。与批次定稿同一次提交写入，复位确认成功才删除，
/// 崩溃或 PLC 连不上都不会让它丢。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261003100000_PendingDeviceResets")]
public partial class PendingDeviceResets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var t = DualColumn.For(migrationBuilder);

        migrationBuilder.CreateTable(
            name: "pending_device_resets",
            columns: table => new
            {
                Id = table.Column<Guid>(type: t.Guid, nullable: false),
                EquipmentId = table.Column<Guid>(type: t.Guid, nullable: false),
                BatchId = table.Column<Guid>(type: t.Guid, nullable: false),
                BatchNo = table.Column<string>(type: t.Text, nullable: false),
                Reason = table.Column<string>(type: t.Text, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: t.Ts, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pending_device_resets", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_pending_device_resets_EquipmentId",
            table: "pending_device_resets",
            column: "EquipmentId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "pending_device_resets");
    }
}
