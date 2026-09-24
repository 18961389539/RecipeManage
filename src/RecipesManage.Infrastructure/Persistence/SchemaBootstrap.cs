using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace RecipesManage.Infrastructure.Persistence;

/// <summary>
/// Applies EF Core migrations. Legacy SQLite files created with EnsureCreated have no
/// __EFMigrationsHistory; their schema is already the current model, so every migration is
/// baselined as applied instead of being replayed on top of an existing schema.
/// </summary>
public static class SchemaBootstrap
{
    public static async Task ApplyAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await IsLegacyEnsureCreatedAsync(db, ct))
            await BaselineAsync(db, ct);

        await db.Database.MigrateAsync(ct);
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
