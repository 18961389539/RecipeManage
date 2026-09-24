using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Identity;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 审计页是服务端分页的表，所以排序只能由后端做：客户端排的是"本页 50 条"，
/// 会给出一个看着完整、其实错的结论。这里同时钉住翻页不重不漏与未知排序键的回退。
/// </summary>
public sealed class AuditQueryTests
{
    [Fact]
    public async Task PagesThroughEveryRowExactlyOnce_NewestFirst()
    {
        await using var db = OpenDb();
        Seed(db, 12);
        var audit = new AuditService(db);

        var seen = new List<Guid>();
        DateTimeOffset? previous = null;
        for (var page = 0; page < 4; page++)
        {
            var result = await audit.QueryAsync(null, null, page * 5, 5, null, null, CancellationToken.None);
            Assert.Equal(12, result.Total);
            foreach (var row in result.Items)
            {
                if (previous is not null)
                    Assert.True(row.At <= previous, $"第 {page + 1} 页出现比上一页更晚的时间");
                previous = row.At;
                seen.Add(row.Id);
            }
        }

        Assert.Equal(12, seen.Distinct().Count());
    }

    [Fact]
    public async Task SortsByWhitelistedColumnAndIgnoresUnknownKeys()
    {
        await using var db = OpenDb();
        Seed(db, 12);
        var audit = new AuditService(db);

        var byUser = await audit.QueryAsync(null, null, 0, 50, "userName", "asc", CancellationToken.None);
        Assert.Equal(byUser.Items.Select(x => x.UserName).OrderBy(x => x, StringComparer.Ordinal),
            byUser.Items.Select(x => x.UserName));

        // 排序键是从前端来的，不能进 SQL：白名单外的值一律回退到默认的时间倒序。
        var injected = await audit.QueryAsync(
            null, null, 0, 50, "(SELECT 1)--)--", "whatever", CancellationToken.None);
        var latest = await audit.QueryAsync(null, null, 0, 1, null, null, CancellationToken.None);
        Assert.Equal(latest.Items.Single().Id, injected.Items.First().Id);
        // 越界的 take/skip 一律收敛，不能拼成非法 SQL 或负数偏移。
        Assert.Equal(12, (await audit.QueryAsync(null, null, -5, 9_000, null, null, CancellationToken.None)).Items.Count);
    }

    private static void Seed(AppDbContext db, int rows)
    {
        for (var i = 0; i < rows; i++)
        {
            db.AuditLogs.Add(new AuditLog(
                null, $"user{i % 3}", $"audit.test.{i}", "Test", i.ToString(), $"详情 {i}"));
            db.SaveChanges();
        }
    }

    private static AppDbContext OpenDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"brmes-auditq-{Guid.NewGuid():N}.db")}")
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
