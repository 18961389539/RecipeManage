using Microsoft.EntityFrameworkCore;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 审计履历的时间必须能在 SQL 侧比较与排序，否则每次翻履历都要把整张表读进内存。
/// 这里同时钉住两件事：映射没有退回原生 DateTimeOffset（退回后第一条会抛 NotSupportedException），
/// 以及存的字符串仍是"字典序即时间序"的写法——三种历史写法（定宽 7 位分数、无分数、被截短的分数）
/// 混在一张表里也要排对，所以不需要回填。
/// </summary>
public sealed class AuditTimestampTests
{
    /// <summary>按时间升序写的三种历史格式：无小数秒、定宽 7 位、被截短。</summary>
    private static readonly (string Detail, string At)[] History =
    [
        ("无分数", "2026-09-22 05:24:53+00:00"),
        ("定宽分数", "2026-09-22 05:24:53.1000000+00:00"),
        ("截短分数", "2026-09-22 05:24:53.25+00:00"),
    ];

    [Fact]
    public async Task OrderingAndComparingAtIsTranslatedToSql()
    {
        await using var db = OpenDb();
        await SeedRawAsync(db);
        db.ChangeTracker.Clear();

        var expected = History.Select(h => h.Detail).ToList();
        var ascending = await db.AuditLogs.OrderBy(a => a.At).Select(a => a.Detail!).ToListAsync();
        Assert.Equal(expected, ascending);

        var descending = await db.AuditLogs.OrderByDescending(a => a.At).Select(a => a.Detail!).ToListAsync();
        Assert.Equal(expected.AsEnumerable().Reverse().ToList(), descending);

        // 范围比较与 SQL 侧分页：走内存的话这两条永远"看起来对"，测不出回归。
        // 53.250 秒：无分数(.0) 与定宽分数(.1) 都小于它，只有截短分数(.25) 命中。
        var since = new DateTimeOffset(2026, 9, 22, 5, 24, 53, 250, TimeSpan.Zero);
        var matching = await db.AuditLogs.Where(a => a.At >= since).Select(a => a.Detail!).ToListAsync();
        Assert.Equal(["截短分数"], matching);
        Assert.Equal("定宽分数", await db.AuditLogs.OrderBy(a => a.At).Skip(1).Select(a => a.Detail!).FirstAsync());

        // 读回来的值仍带正确偏移与精度：被截短的 .25 是 250 毫秒，不是 25 毫秒。
        var row = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Detail == "截短分数");
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 5, 24, 53, 250, TimeSpan.Zero), row.At);
    }

    [Fact]
    public async Task NewWritesKeepTheComparableCanonicalText()
    {
        await using var db = OpenDb();
        db.AuditLogs.Add(new RecipesManage.Domain.Identity.AuditLog(
            null, "system", "audit.test", "Test", "row", "详情"));
        await db.SaveChangesAsync();

        // 域层写的是 UtcNow，这里钉的是"写出去的字符串仍是可字典序比较的那一种"：
        // 格式一改，新行与历史行混排就会错序，而这个错序只有查履历时才看得见。
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(\.\d{1,7})?\+00:00$", await RawAtAsync(db));
    }

    private static async Task SeedRawAsync(AppDbContext db)
    {
        foreach (var (detail, at) in History)
        {
            var id = Guid.NewGuid().ToString();
            await db.Database.ExecuteSqlRawAsync(
                """INSERT INTO "audit_logs" ("Id", "UserId", "UserName", "Action", "EntityType", "EntityId", "Detail", "At", "CreatedAt", "UpdatedAt") VALUES ({0}, NULL, 'system', 'audit.test', 'Test', {0}, {1}, {2}, '2026-09-22 05:24:53', NULL);""",
                [id, detail, at], CancellationToken.None);
        }
    }

    private static async Task<string> RawAtAsync(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "At" FROM "audit_logs" LIMIT 1""";
        return (string?)await command.ExecuteScalarAsync(CancellationToken.None) ?? "";
    }

    private static AppDbContext OpenDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"brmes-audit-{Guid.NewGuid():N}.db")}")
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
