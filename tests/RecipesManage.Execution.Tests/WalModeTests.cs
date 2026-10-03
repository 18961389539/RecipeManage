using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RecipesManage.Domain.Identity;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 库跑在 WAL 模式下的几个事实。EF 的 <c>EnsureCreated</c> 会顺手设 WAL、<c>Migrate</c> 不会，
/// 所以"新装机器是回滚日志模式"这件事曾经无声无息地存在——这里把"开机后一定是 WAL"钉住，
/// 并钉住 WAL 带来的两个后果：备份仍是单文件、维护作业不留下膨胀的 -wal。
/// </summary>
public sealed class WalModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"brmes-wal-{Guid.NewGuid():N}");

    public WalModeTests() => Directory.CreateDirectory(_root);

    private static DbContextOptions<AppDbContext> Options(string path) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;

    private static string JournalMode(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        return (string)command.ExecuteScalar()!;
    }

    private static void ForceJournalMode(string path, string mode)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA journal_mode={mode}";
        command.ExecuteNonQuery();
    }

    private static long AuditCount(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_logs";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public async Task ABrandNewDatabaseEndsUpInWal_AfterTheNormalStartup()
    {
        var path = Path.Combine(_root, "fresh.db");
        await using (var db = new AppDbContext(Options(path)))
            await SchemaBootstrap.ApplyAsync(db);

        Assert.Equal("wal", JournalMode(path), ignoreCase: true);
    }

    [Fact]
    public async Task ARollbackJournalDatabaseFromAnOlderInstall_IsConvertedWithoutLosingData()
    {
        var path = Path.Combine(_root, "legacy.db");
        // 旧版装机的形态：只 Migrate，没人设过 WAL。
        await using (var db = new AppDbContext(Options(path)))
        {
            await db.Database.MigrateAsync();
            db.AuditLogs.Add(new AuditLog(null, "tester", "legacy.row", "Test", "1", "迁移前就有的一行"));
            await db.SaveChangesAsync();
        }
        SqliteConnection.ClearAllPools();
        ForceJournalMode(path, "DELETE");   // 直接 Migrate 建出来的库 EF 会顺手设成 WAL，这里还原成旧装机的形态
        SqliteConnection.ClearAllPools();
        Assert.Equal("delete", JournalMode(path), ignoreCase: true);

        await using (var db = new AppDbContext(Options(path)))
            await SchemaBootstrap.ApplyAsync(db);
        SqliteConnection.ClearAllPools();

        Assert.Equal("wal", JournalMode(path), ignoreCase: true);
        Assert.Equal(1, AuditCount(path));
    }

    [Fact]
    public async Task ABackupOfAWalDatabase_IsStillOneSelfContainedFile_WithTheLatestCommits()
    {
        var source = Path.Combine(_root, "recipes.db");
        await using var db = new AppDbContext(Options(source));
        await SchemaBootstrap.ApplyAsync(db);
        // 这几行还在 -wal 里、没合并进主库——裸拷 .db 会丢掉它们，Backup API 不会。
        for (var i = 0; i < 20; i++)
            db.AuditLogs.Add(new AuditLog(null, "tester", "wal.row", "Test", i.ToString(), "还在 WAL 里"));
        await db.SaveChangesAsync();
        Assert.Equal("wal", JournalMode(source), ignoreCase: true);

        var dir = Path.Combine(_root, "backups");
        var backup = new DatabaseBackup(
            new BackupSettings { Keep = 7, Directory = dir, AtUtc = new TimeOnly(2, 15) }, $"Data Source={source}");
        var file = backup.Run();
        var target = Path.Combine(dir, file.Name);

        Assert.False(File.Exists(target + "-wal"));
        Assert.False(File.Exists(target + "-shm"));
        Assert.NotEqual("wal", JournalMode(target), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(20, AuditCount(target));
    }

    [Fact]
    public void Vacuum_InWal_DoesNotLeaveAnOversizedWalBehind()
    {
        var path = Path.Combine(_root, "vac.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            Exec(connection, "PRAGMA journal_mode=WAL");
            Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, blob TEXT)");
            using var tx = connection.BeginTransaction();
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO t(blob) VALUES (@v)";
                var value = insert.Parameters.Add("@v", SqliteType.Text);
                for (var i = 0; i < 3_000; i++)
                {
                    value.Value = new string('x', 200);
                    insert.ExecuteNonQuery();
                }
            }
            tx.Commit();
            Exec(connection, "DELETE FROM t WHERE id > 1000");
        }

        // 另有一条连接一直开着：否则 Run 自己那条连接关闭时 SQLite 会顺手合并并删掉 -wal，
        // 测试就测不出 VACUUM 之后那次截断检查点到底有没有做。
        using var keeper = new SqliteConnection($"Data Source={path};Pooling=False");
        keeper.Open();
        Exec(keeper, "SELECT COUNT(*) FROM t");

        var result = new DatabaseMaintenance($"Data Source={path}", new MaintenanceSettings { MinFreeMegabytes = 0 })
            .Run(idleForVacuum: true);

        Assert.True(result.Vacuumed, result.SkippedReason ?? "");
        var walPath = path + "-wal";
        // 截断检查点之后 WAL 应当是空的（或已随最后一条连接关闭而删除）。
        var walBytes = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        Assert.True(walBytes == 0, $"-wal 还有 {walBytes} 字节");
        Assert.Equal("wal", JournalMode(path), ignoreCase: true);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录留在 %TEMP%，比让测试变红合适。
        }
    }
}
