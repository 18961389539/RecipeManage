using Microsoft.EntityFrameworkCore;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 模型与迁移快照不一致的守卫。
///
/// 这个项目的迁移有不少是手写的（没有 .Designer.cs），快照也是手工同步：漏改一个索引或列，
/// 运行时一切正常，直到下一个人 <c>dotnet ef migrations add</c> 时凭空多出一条"补差"迁移——
/// 而那条迁移可能去删一个线上真在用的索引。这里让这种漂移在 CI 里当场红。
/// </summary>
public sealed class ModelSnapshotInSyncTests
{
    [Fact]
    public void Entity_model_matches_the_latest_migration_snapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"brmes-snapshot-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
        using var db = new AppDbContext(options);

        Assert.False(
            db.Database.HasPendingModelChanges(),
            "实体模型与 AppDbContextModelSnapshot 不一致：有人改了实体/映射却没同步迁移快照。");
    }
}
