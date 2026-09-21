using Microsoft.EntityFrameworkCore;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

[CollectionDefinition("RecipesDatabaseEnv")]
public sealed class RecipesDatabaseEnvCollection
{
}

[Collection("RecipesDatabaseEnv")]
public sealed class RecipesDatabaseTests
{
    [Fact]
    public void Resolve_UsesPostgresWhenConnectionStringPresent()
    {
        var (provider, cs) = RecipesDatabase.Resolve(
            "Host=localhost;Database=brmes;Username=brmes;Password=secret",
            "Data Source=x.db");
        Assert.Equal(RecipesDatabase.PostgreSql, provider);
        Assert.Contains("brmes", cs, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DefaultsToSqliteWhenPostgresBlank()
    {
        var prev = Environment.GetEnvironmentVariable("BRMES_POSTGRES");
        try
        {
            Environment.SetEnvironmentVariable("BRMES_POSTGRES", null);
            var (provider, cs) = RecipesDatabase.Resolve("  ", null);
            Assert.Equal(RecipesDatabase.Sqlite, provider);
            Assert.Contains("recipes.db", cs, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRMES_POSTGRES", prev);
        }
    }

    [Fact]
    public void Resolve_UsesBrmesPostgresEnvironment()
    {
        var prev = Environment.GetEnvironmentVariable("BRMES_POSTGRES");
        try
        {
            Environment.SetEnvironmentVariable("BRMES_POSTGRES", "Host=127.0.0.1;Database=fromenv");
            var (provider, cs) = RecipesDatabase.Resolve(null, "Data Source=x.db");
            Assert.Equal(RecipesDatabase.PostgreSql, provider);
            Assert.Contains("fromenv", cs, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRMES_POSTGRES", prev);
        }
    }

    [Fact]
    public void EmbeddedFlag_StartsOnlyWhenRequestedAndNoConnection()
    {
        var prevCs = Environment.GetEnvironmentVariable("BRMES_POSTGRES");
        var prevFlag = Environment.GetEnvironmentVariable("BRMES_EMBEDDED_POSTGRES");
        try
        {
            Environment.SetEnvironmentVariable("BRMES_POSTGRES", null);
            Environment.SetEnvironmentVariable("BRMES_EMBEDDED_POSTGRES", "1");
            Assert.True(EmbeddedPostgresRuntime.ShouldStart());
            Environment.SetEnvironmentVariable("BRMES_EMBEDDED_POSTGRES", null);
            Assert.False(EmbeddedPostgresRuntime.ShouldStart());
            Environment.SetEnvironmentVariable("BRMES_POSTGRES", "Host=127.0.0.1;Database=x");
            Environment.SetEnvironmentVariable("BRMES_EMBEDDED_POSTGRES", "1");
            Assert.False(EmbeddedPostgresRuntime.ShouldStart());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRMES_POSTGRES", prevCs);
            Environment.SetEnvironmentVariable("BRMES_EMBEDDED_POSTGRES", prevFlag);
        }
    }

    [Fact]
    public void TryGetSqliteFilePath_NullWhenPostgres()
    {
        var prev = Environment.GetEnvironmentVariable("BRMES_POSTGRES");
        try
        {
            Environment.SetEnvironmentVariable("BRMES_POSTGRES", null);
            Assert.Null(RecipesDatabase.TryGetSqliteFilePath("Host=localhost;Database=brmes", null));
            var path = RecipesDatabase.TryGetSqliteFilePath(null, "Data Source=App_Data/recipes.db");
            Assert.NotNull(path);
            Assert.EndsWith("recipes.db", path, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRMES_POSTGRES", prev);
        }
    }

    [Fact]
    public void HealthBody_ReportsJsonbOnPostgreSqlAndTextOnSqlite()
    {
        var pg = System.Text.Json.JsonSerializer.Serialize(RecipesDatabase.HealthBody(RecipesDatabase.PostgreSql, true));
        Assert.Contains("postgresql", pg, StringComparison.Ordinal);
        Assert.Contains("jsonb", pg, StringComparison.Ordinal);
        var sqlite = System.Text.Json.JsonSerializer.Serialize(RecipesDatabase.HealthBody(RecipesDatabase.Sqlite, true));
        Assert.Contains("\"text\"", sqlite, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_PostgreSql_MapsControlRecipeJsonToJsonb()
    {
        var builder = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>();
        RecipesDatabase.Apply(builder, "Host=127.0.0.1;Database=brmes_unused;Username=brmes;Password=brmes", null);
        using var db = new AppDbContext(builder.Options);
        Assert.Equal(RecipesDatabase.PostgreSql, RecipesDatabase.HealthName(db.Database));
        var column = db.Model.FindEntityType(typeof(RecipesManage.Domain.Batches.ProductionBatch))!
            .FindProperty(nameof(RecipesManage.Domain.Batches.ProductionBatch.ControlRecipeJson))!
            .GetColumnType();
        Assert.Equal("jsonb", column);
    }

    [Fact]
    public async Task Migrate_Sqlite_CreatesHistoryAndAppliesModel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"brmes-mig-{Guid.NewGuid():N}.db");
        var prev = Environment.GetEnvironmentVariable("BRMES_POSTGRES");
        try
        {
            Environment.SetEnvironmentVariable("BRMES_POSTGRES", null);
            var builder = new DbContextOptionsBuilder<AppDbContext>();
            RecipesDatabase.Apply(builder, null, $"Data Source={path}");
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
            Environment.SetEnvironmentVariable("BRMES_POSTGRES", prev);
            try { File.Delete(path); } catch { /* temp */ }
        }
    }
}
