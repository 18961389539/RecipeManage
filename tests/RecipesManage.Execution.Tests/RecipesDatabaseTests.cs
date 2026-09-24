using Microsoft.EntityFrameworkCore;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

public sealed class RecipesDatabaseTests
{
    [Fact]
    public void ResolveConnectionString_DefaultsToAppDataSqlite()
    {
        Assert.Contains("recipes.db", RecipesDatabase.ResolveConnectionString(null), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Data Source=x.db", RecipesDatabase.ResolveConnectionString(" Data Source=x.db "));
    }

    [Fact]
    public void TryGetSqliteFilePath_ResolvesRelativeAndSkipsMemory()
    {
        var path = RecipesDatabase.TryGetSqliteFilePath("Data Source=App_Data/recipes.db");
        Assert.NotNull(path);
        Assert.EndsWith("recipes.db", path, StringComparison.OrdinalIgnoreCase);
        Assert.Null(RecipesDatabase.TryGetSqliteFilePath("Data Source=:memory:"));
        Assert.Null(RecipesDatabase.TryGetSqliteFilePath("Data Source=file::memory:?cache=shared"));
    }

    [Fact]
    public void HealthBody_ReportsSqliteAndText()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(RecipesDatabase.HealthBody(RecipesDatabase.Sqlite, true));
        Assert.Contains("\"sqlite\"", json, StringComparison.Ordinal);
        Assert.Contains("\"TEXT\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrate_Sqlite_CreatesHistoryAndAppliesModel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"brmes-mig-{Guid.NewGuid():N}.db");
        try
        {
            var builder = new DbContextOptionsBuilder<AppDbContext>();
            RecipesDatabase.Apply(builder, $"Data Source={path}");
            await using var db = new AppDbContext(builder.Options);
            await SchemaBootstrap.ApplyAsync(db);
            Assert.Contains("Initial", string.Join(",", db.Database.GetAppliedMigrations()));
            Assert.True(await db.Database.CanConnectAsync());
            var json = db.Model.FindEntityType(typeof(RecipesManage.Domain.Batches.ProductionBatch))!
                .FindProperty(nameof(RecipesManage.Domain.Batches.ProductionBatch.ControlRecipeJson))!
                .GetColumnType();
            Assert.Equal("TEXT", json);
            Assert.False(await SchemaBootstrap.IsLegacyEnsureCreatedAsync(db));
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp */ }
        }
    }
}
