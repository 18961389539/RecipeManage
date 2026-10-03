using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace RecipesManage.Infrastructure.Persistence;

/// <summary>
/// 每条连接打开时设置的 SQLite 连接级参数。
///
/// 为什么需要：<c>busy_timeout</c> 是连接级设置，不跟库文件走，新开的连接默认是 0 ——
/// 也就是别的连接正在写时，这条连接<strong>立刻</strong>报 <c>SQLITE_BUSY</c>（"database is locked"），而不是等一等。
/// 调度引擎每条车道各开一个连接、HTTP 请求、备份、维护同时在写同一个文件，
/// 默认值下一次碰巧的重叠就能把一个所有工步都已完成的批次置成 Faulted（压力复现过）。
/// Microsoft.Data.Sqlite 的 <c>Default Timeout</c> 只管语句级重试，管不到提交 / 重置阶段的 BUSY。
/// </summary>
public sealed class SqliteConnectionPragmas : DbConnectionInterceptor
{
    public const int DefaultBusyTimeoutMs = 10_000;

    private readonly string _sql;

    public SqliteConnectionPragmas(int busyTimeoutMs = DefaultBusyTimeoutMs) =>
        _sql = $"PRAGMA busy_timeout={Math.Clamp(busyTimeoutMs, 0, 300_000)};";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        Apply(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = _sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = _sql;
        command.ExecuteNonQuery();
    }
}
