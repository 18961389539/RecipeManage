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

    /// <summary>
    /// 现场支持要能在屏幕上读出"哪一版、库结构升到哪儿"。这两件事以前只能靠人去翻 exe 属性。
    /// </summary>
    [Fact]
    public void HealthBody_CarriesVersionAndMigrationWatermark()
    {
        Assert.False(string.IsNullOrWhiteSpace(RecipesDatabase.Version));
        Assert.NotEqual("unknown", RecipesDatabase.Version);

        var json = System.Text.Json.JsonSerializer.Serialize(RecipesDatabase.HealthBody(
            RecipesDatabase.Sqlite, true,
            new RecipesDatabase.MigrationWatermark("20260925000000_HandshakeEventTimeline", 0)));

        Assert.Contains(RecipesDatabase.Version, json, StringComparison.Ordinal);
        Assert.Contains("20260925000000_HandshakeEventTimeline", json, StringComparison.Ordinal);
        Assert.Contains("\"pendingMigrations\":0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UnhealthyHealthBody_StillIdentifiesTheBuild()
    {
        // 探活失败时恰恰最需要知道版本，别把诊断信息一起丢了。
        var json = System.Text.Json.JsonSerializer.Serialize(RecipesDatabase.HealthBody(RecipesDatabase.Sqlite, false));
        Assert.Contains("\"unhealthy\"", json, StringComparison.Ordinal);
        Assert.Contains(RecipesDatabase.Version, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MigrationWatermark_CountsWhatIsStillToCome()
    {
        // 升到底之后：水位指向最后一条迁移，待应用为 0。
        var currentPath = Path.Combine(Path.GetTempPath(), $"brmes-watermark-{Guid.NewGuid():N}.db");
        // 停在最早一条的库（现场老设备的真实形状）。SQLite 不支持 Down，所以只能正向建、不能倒着升。
        var behindPath = Path.Combine(Path.GetTempPath(), $"brmes-behind-{Guid.NewGuid():N}.db");
        try
        {
            var builder = new DbContextOptionsBuilder<AppDbContext>();
            RecipesDatabase.Apply(builder, $"Data Source={currentPath}");
            await using var db = new AppDbContext(builder.Options);
            var all = db.Database.GetMigrations().ToArray();

            await SchemaBootstrap.ApplyAsync(db);
            var current = await RecipesDatabase.SafeMigrationWatermarkAsync(db);
            Assert.NotNull(current);
            Assert.Equal(all.Last(), current!.Applied);
            Assert.Equal(0, current.PendingCount);

            var behindBuilder = new DbContextOptionsBuilder<AppDbContext>();
            RecipesDatabase.Apply(behindBuilder, $"Data Source={behindPath}");
            await using var behindDb = new AppDbContext(behindBuilder.Options);
            await behindDb.Database.MigrateAsync(all.First());
            var behind = await RecipesDatabase.SafeMigrationWatermarkAsync(behindDb);
            Assert.NotNull(behind);
            Assert.Equal(all.First(), behind!.Applied);
            Assert.Equal(all.Length - 1, behind.PendingCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { currentPath, behindPath })
                try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* 临时目录 */ }
        }
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
