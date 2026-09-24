using Microsoft.EntityFrameworkCore;
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
}
