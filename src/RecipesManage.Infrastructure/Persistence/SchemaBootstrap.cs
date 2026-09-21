using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace RecipesManage.Infrastructure.Persistence;

/// <summary>
/// Applies EF Core migrations. Legacy SQLite files created with EnsureCreated have no
/// __EFMigrationsHistory; those are baselined so subsequent migrations can run.
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
        var initial = db.Database.GetMigrations().FirstOrDefault()
                      ?? throw new InvalidOperationException("未找到 EF Core 迁移程序集。");

        if (RecipesDatabase.HealthName(db.Database) == RecipesDatabase.PostgreSql)
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                );
                """, ct);
            await db.Database.ExecuteSqlRawAsync(
                """INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ({0}, {1}) ON CONFLICT DO NOTHING;""",
                [initial, "10.0.11"], ct);
            return;
        }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            """, ct);
        await db.Database.ExecuteSqlRawAsync(
            """INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ({0}, {1});""",
            [initial, "10.0.11"], ct);
    }

    private static async Task<HashSet<string>> ListTablesAsync(AppDbContext db, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sql = RecipesDatabase.HealthName(db.Database) == RecipesDatabase.PostgreSql
            ? """SELECT tablename FROM pg_tables WHERE schemaname = 'public'"""
            : """SELECT name FROM sqlite_master WHERE type = 'table'""";

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await db.Database.OpenConnectionAsync(ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));
        return names;
    }
}
