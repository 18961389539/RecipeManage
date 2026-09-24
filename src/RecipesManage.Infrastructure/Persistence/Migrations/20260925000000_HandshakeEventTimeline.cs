using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// 握手履历改为"只取最近 N 条"，为此需要一条能按时间倒序扫的索引。
///
/// 为什么：一个长跑批次的握手事件是每工步十几条起步、按 100ms 一拍累积的量，
/// 而 <c>GET /batches/{id}/handshake-log</c> 以前是整表读进内存再排序——监控页每 4 秒就来一次。
/// SQLite 提供器不允许对原生 DateTimeOffset 列 ORDER BY，所以 <c>CreatedAt</c> 现在也走
/// <see cref="Persistence.AuditTimestamp"/> 的定宽 UTC 文本映射（与 EF 自带映射逐字符一致，历史行无需回填），
/// 于是 "WHERE BatchId = @id ORDER BY CreatedAt DESC LIMIT @n" 能整条下推。
///
/// 只留复合索引：最左前缀就是 BatchId，原来那条单列索引对查询是冗余的（与 process_samples 同一形状）。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260925000000_HandshakeEventTimeline")]
public partial class HandshakeEventTimeline : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_handshake_events_BatchId",
            table: "handshake_events");

        migrationBuilder.CreateIndex(
            name: "IX_handshake_events_BatchId_CreatedAt",
            table: "handshake_events",
            columns: new[] { "BatchId", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_handshake_events_BatchId_CreatedAt",
            table: "handshake_events");

        migrationBuilder.CreateIndex(
            name: "IX_handshake_events_BatchId",
            table: "handshake_events",
            column: "BatchId");
    }
}
