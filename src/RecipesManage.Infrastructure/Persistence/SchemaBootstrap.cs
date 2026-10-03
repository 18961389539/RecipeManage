using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace RecipesManage.Infrastructure.Persistence;

/// <summary>
/// Applies EF Core migrations. Legacy SQLite files created with EnsureCreated have no
/// __EFMigrationsHistory; their schema is already the current model, so every migration is
/// baselined as applied instead of being replayed on top of an existing schema.
/// </summary>
public static class SchemaBootstrap
{
    /// <summary>
    /// 开机升结构。<paramref name="backup"/> 给了就必须先落一份**已校验**的升级前快照才允许动手。
    ///
    /// 为什么这条防线不能省：这个库的迁移是手写的，<c>Down()</c> 写好了却没有任何路径会去调它，
    /// 所以"回滚"实际等于"恢复快照"。没有快照的升级是一次单程票——升到一半 SQL 失败，
    /// 库就停在半应用状态，而那台设备当天可能还在跑批。
    /// 全新空库不在此列：没有旧结构可破坏，也没必要为一堆 0 字节的文件留快照。
    /// </summary>
    public static async Task ApplyAsync(
        AppDbContext db,
        DatabaseBackup? backup = null,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        if (await IsLegacyEnsureCreatedAsync(db, ct))
            await BaselineAsync(db, ct);

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToArray();
        if (pending.Length > 0 && (await ListTablesAsync(db, ct)).Count > 0)
        {
            if (backup is null)
                throw new InvalidOperationException(
                    $"有 {pending.Length} 条待应用迁移，但没有可用的备份组件：拒绝在没有升级前快照的情况下改库结构。");

            BackupFile snapshot;
            try
            {
                snapshot = backup.SnapshotBeforeMigration();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // 快照写不成（盘满、目录不可写、源库打不开）就停在这儿：
                // 宁可今天不升级，也不要拿一个没有退路的库去改结构。
                throw new InvalidOperationException(
                    $"升级前快照没写成，因此拒绝执行 {pending.Length} 条迁移。请先解决备份目录 " +
                    $"\u201c{backup.PreMigrationDirectory}\u201d 的写入问题，再重启服务。", e);
            }

            log?.LogWarning(
                "即将应用 {Count} 条迁移（先到 {Last}）。升级前快照 {File}（{Bytes} 字节）已校验，" +
                "升坏了请停应用，先移走旧的 recipes.db-wal / recipes.db-shm，再用该文件覆盖数据库文件恢复。",
                pending.Length, pending.Last(), snapshot.Name, snapshot.Bytes);
        }

        await db.Database.MigrateAsync(ct);

        // 放在迁移之后：结构升级期间库的形态不变，升级前快照与迁移本身都按原来的模式跑；
        // 升完再切，新装机器和从旧版升上来的机器最终都落在 WAL。
        await SqliteJournal.EnsureWalAsync(db, log, ct);
    }

    public static async Task<bool> IsLegacyEnsureCreatedAsync(AppDbContext db, CancellationToken ct = default)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(ct))
            return false;

        var tables = await ListTablesAsync(db, ct);
        return tables.Contains("users") && !tables.Contains("__EFMigrationsHistory");
    }

    private static async Task BaselineAsync(AppDbContext db, CancellationToken ct)
    {
        var migrations = db.Database.GetMigrations().ToArray();
        if (migrations.Length == 0)
            throw new InvalidOperationException("未找到 EF Core 迁移程序集。");

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            """, ct);

        // 只登记第一条迁移是个坑：MigrateAsync 会把后面每条都对着"已经由 EnsureCreated
        // 按当前模型建好的库"再执行一遍，第一条 ALTER TABLE ADD COLUMN 就撞已存在的列，
        // 启动直接崩在半应用状态。EnsureCreated 的库模式即当前模型，所以整体视为已应用。
        var productVersion = db.Model.GetProductVersion() ?? "10.0.0";
        foreach (var migration in migrations)
        {
            await db.Database.ExecuteSqlRawAsync(
                """INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ({0}, {1});""",
                [migration, productVersion], ct);
        }
    }

    private static async Task<HashSet<string>> ListTablesAsync(AppDbContext db, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """SELECT name FROM sqlite_master WHERE type = 'table'""";
        await db.Database.OpenConnectionAsync(ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));
        return names;
    }
}
