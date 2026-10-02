using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 老的 EnsureCreated 库没有迁移历史表，基线必须把"当前全部迁移"记成已应用。
/// 只记第一条的话，MigrateAsync 会把后面的每条迁移对着已经建好的模式重放，
/// 第一条 ALTER TABLE ADD COLUMN 就撞已存在的列——启动失败在半应用状态上。
/// </summary>
public sealed class SchemaBootstrapTests
{
    [Fact]
    public async Task LegacyEnsureCreatedDatabase_BaselinesEveryMigration()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"brmes-legacy-{Guid.NewGuid():N}.db")}")
            .Options;
        await using (var created = new AppDbContext(options))
            await created.Database.EnsureCreatedAsync();

        await using var db = new AppDbContext(options);
        Assert.True(await SchemaBootstrap.IsLegacyEnsureCreatedAsync(db));

        await SchemaBootstrap.ApplyAsync(db);

        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.False(await SchemaBootstrap.IsLegacyEnsureCreatedAsync(db));
    }

    private sealed record Fixture(string Root, string DbPath, string ConnectionString) : IDisposable
    {
        public static Fixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"brmes-upgrade-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var dbPath = Path.Combine(root, "recipes.db");
            return new Fixture(root, dbPath, $"Data Source={dbPath}");
        }

        public DbContextOptions<AppDbContext> Options() =>
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ConnectionString).Options;

        public void Dispose()
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException) { /* 留在 %TEMP% 里比让测试变红合适 */ }
        }
    }

    /// <summary>
    /// 造一个"停在最早那条迁移"的库，并往里留一行可辨认的数据 —— 这就是现场升级前的真实形状。
    /// </summary>
    private static async Task<(Fixture Fix, int Rows)> OldSchemaAsync()
    {
        var fix = Fixture.Create();
        var first = new AppDbContext(fix.Options()).Database.GetMigrations().First();
        await using (var old = new AppDbContext(fix.Options()))
        {
            await old.Database.MigrateAsync(first);
            old.AuditLogs.Add(new RecipesManage.Domain.Identity.AuditLog(
                null, "tester", "audit.upgrade", "Test", "1", "升级前就在那儿的一行"));
            await old.SaveChangesAsync();
        }
        return (fix, 1);
    }

    [Fact]
    public async Task PendingMigrations_TakeAVerifiedSnapshotFirst()
    {
        var (fix, rows) = await OldSchemaAsync();
        try
        {
            var backup = new DatabaseBackup(
                new BackupSettings { Directory = Path.Combine(fix.Root, "backups") }, fix.ConnectionString);
            await using (var db = new AppDbContext(fix.Options()))
            {
                Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());
                await SchemaBootstrap.ApplyAsync(db, backup, NullLogger.Instance);
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }

            // 快照落在 pre-migration 子目录里，且必须真的能打开、带着升级前那行数据。
            var snapshots = backup.ListBackups(backup.PreMigrationDirectory);
            Assert.Single(snapshots);
            Assert.StartsWith("brmes-", snapshots[0].Name, StringComparison.Ordinal);
            await using var restored = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(backup.PreMigrationDirectory, snapshots[0].Name)}").Options);
            Assert.Equal(rows, await restored.AuditLogs.CountAsync(a => a.Action == "audit.upgrade"));
        }
        finally { fix.Dispose(); }
    }

    [Fact]
    public async Task UpgradeSnapshot_IsInvisibleToTheDailyBackupListAndRotation()
    {
        var (fix, _) = await OldSchemaAsync();
        try
        {
            var backup = new DatabaseBackup(
                new BackupSettings { Directory = Path.Combine(fix.Root, "backups"), Keep = 1 }, fix.ConnectionString);
            await using (var db = new AppDbContext(fix.Options()))
                await SchemaBootstrap.ApplyAsync(db, backup, NullLogger.Instance);

            // 每日快照的列表/裁剪都不看子目录：升级退路不会被"Keep=1"顺手裁掉。
            Assert.Empty(backup.ListBackups());
            Assert.Single(backup.ListBackups(backup.PreMigrationDirectory));
            Assert.Empty(backup.PruneOldBackups());
            Assert.Single(backup.ListBackups(backup.PreMigrationDirectory));
        }
        finally { fix.Dispose(); }
    }

    [Fact]
    public async Task WithoutABackupComponent_TheSchemaIsNotTouched()
    {
        var (fix, _) = await OldSchemaAsync();
        try
        {
            await using var db = new AppDbContext(fix.Options());
            var before = (await db.Database.GetPendingMigrationsAsync()).Count();
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaBootstrap.ApplyAsync(db));
            Assert.Contains("拒绝", e.Message, StringComparison.Ordinal);

            await using var probe = new AppDbContext(fix.Options());
            Assert.Equal(before, (await probe.Database.GetPendingMigrationsAsync()).Count());
        }
        finally { fix.Dispose(); }
    }

    [Fact]
    public async Task WhenTheSnapshotCannotBeWritten_TheMigrationIsRefused()
    {
        var (fix, _) = await OldSchemaAsync();
        try
        {
            // 拿一个已存在的普通文件当"备份目录"：建目录必然失败，模拟盘满/权限错了的那一类。
            var blocked = Path.Combine(fix.Root, "blocked");
            File.WriteAllText(blocked, "这是个文件，不是目录");
            var backup = new DatabaseBackup(new BackupSettings { Directory = blocked }, fix.ConnectionString);

            await using var db = new AppDbContext(fix.Options());
            var e = await Assert.ThrowsAsync<InvalidOperationException>(
                () => SchemaBootstrap.ApplyAsync(db, backup, NullLogger.Instance));
            Assert.Contains("拒绝执行", e.Message, StringComparison.Ordinal);

            await using var probe = new AppDbContext(fix.Options());
            Assert.NotEmpty(await probe.Database.GetPendingMigrationsAsync());   // 还停在旧结构上
        }
        finally { fix.Dispose(); }
    }
}
