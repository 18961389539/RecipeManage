using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RecipesManage.Infrastructure.Persistence;

/// <summary>
/// 把库切到 WAL 日志模式。
///
/// 为什么要显式做：EF 只在<strong>它自己创建库文件</strong>时顺手设 WAL（直接 <c>Migrate</c> / <c>EnsureCreated</c>
/// 建出来的库实测是 WAL）。而 <see cref="SchemaBootstrap"/> 迁移之前要先探测库里有哪些表，探测会先把空文件建出来，
/// EF 于是认为库已存在、不再设——新装机器实测落在回滚日志（delete）模式；只有历史上由
/// EnsureCreated 建出来的开发库碰巧是 WAL。而备份、维护、锁相关的代码与注释都按"库跑在 WAL 下"写。
///
/// WAL 的好处在这里具体是：调度车道每个 tick 在写，界面在同时轮询读，回滚日志模式下读者持共享锁、
/// 写者提交要等读者放手；WAL 下读者读快照、写者追加日志，互不等待。写者仍然只有一个，
/// 所以连接级 <see cref="SqliteConnectionPragmas"/> 的 busy_timeout 照样需要。
///
/// 这个设置写进库文件，不是每条连接都要设；但切换需要独占，所以只能在开机、单实例锁已拿到之后做。
/// </summary>
public static class SqliteJournal
{
    /// <summary>
    /// 把已打开（或可打开）的库切到 WAL，返回切换后的实际模式。
    /// 内存库、或文件系统不支持（网络盘）时 SQLite 不会报错、只是留在原模式——
    /// 这里不抛：回滚日志模式功能上完全可用，只是并发差一些，但要让现场看得见。
    /// </summary>
    public static async Task<string> EnsureWalAsync(DbContext db, ILogger? log = null, CancellationToken ct = default)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL";
            var mode = Convert.ToString(await command.ExecuteScalarAsync(ct)) ?? "unknown";

            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                log?.LogWarning(
                    "数据库没能切到 WAL 模式（当前 {Mode}）：继续按回滚日志模式运行，读写并发会更差。" +
                    "常见原因：库文件在网络共享盘上，或有别的进程正占着这个库。", mode);
            return mode;
        }
        finally
        {
            // EF 的打开 / 关闭是计数的：自己开了一次就必须还一次，不管之前是不是已经开着。
            await db.Database.CloseConnectionAsync();
        }
    }
}
