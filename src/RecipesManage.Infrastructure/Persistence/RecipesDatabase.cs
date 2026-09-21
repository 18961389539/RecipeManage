using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace RecipesManage.Infrastructure.Persistence;

public static class RecipesDatabase
{
    public const string Sqlite = "sqlite";
    public const string PostgreSql = "postgresql";

    public static (string Provider, string ConnectionString) Resolve(string? postgres, string? sqlite)
    {
        if (string.IsNullOrWhiteSpace(postgres))
            postgres = Environment.GetEnvironmentVariable("BRMES_POSTGRES");
        if (!string.IsNullOrWhiteSpace(postgres))
            return (PostgreSql, postgres.Trim());
        return (Sqlite, string.IsNullOrWhiteSpace(sqlite) ? "Data Source=App_Data/recipes.db" : sqlite.Trim());
    }

    public static string HealthName(DatabaseFacade database)
    {
        var name = database.ProviderName ?? "";
        if (name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase))
            return PostgreSql;
        return Sqlite;
    }

    public static string ControlRecipeStorage(string provider) =>
        provider == PostgreSql ? "jsonb" : "text";

    public static object HealthBody(string provider, bool ok)
    {
        var recipe = ControlRecipeStorage(provider);
        return ok
            ? new { status = "ok", database = provider, engine = "brmes", controlRecipe = recipe }
            : new { status = "unhealthy", database = provider, controlRecipe = recipe };
    }

    public static bool IsPostgreSql(this DbContext db) =>
        HealthName(db.Database) == PostgreSql;

    public static string? TryGetSqliteFilePath(string? postgres, string? sqlite)
    {
        var (provider, cs) = Resolve(postgres, sqlite);
        if (provider != Sqlite)
            return null;
        var path = cs.Replace("Data Source=", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (path.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase) || path is ":memory:")
            return null;
        return Path.GetFullPath(path);
    }

    public static void Apply(DbContextOptionsBuilder options, string? postgres, string? sqlite)
    {
        var (provider, cs) = Resolve(postgres, sqlite);
        if (provider == PostgreSql)
        {
            options.UseNpgsql(cs, npg => npg.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
            // One Initial migration is authored against SQLite types; Up() emits uuid/jsonb for Npgsql.
            // The sqlite snapshot therefore always looks "pending" on PostgreSQL — do not block migrate.
            options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        }
        else
        {
            EnsureSqliteDirectory(cs);
            options.UseSqlite(cs, sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
        }
    }

    public static void EnsureSqliteDirectory(string connectionString)
    {
        var path = connectionString.Replace("Data Source=", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (path.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase) || path is ":memory:")
            return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
