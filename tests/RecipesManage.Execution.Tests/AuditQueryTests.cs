using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 审计页是服务端分页的表，所以排序与搜索都只能由后端做：客户端排/搜的是"本页 50 条"，
/// 会给出一个看着完整、其实错的结论。这里同时钉住翻页不重不漏、未知排序键的回退，
/// 以及关键词全库检索（含 LIKE 通配符转义与中文标签反查 code）。
/// </summary>
public sealed class AuditQueryTests
{
    [Fact]
    public async Task PagesThroughEveryRowExactlyOnce_NewestFirst()
    {
        await using var db = OpenDb();
        Seed(db, 12);
        var audit = QualityAudit(db);

        var seen = new List<Guid>();
        DateTimeOffset? previous = null;
        for (var page = 0; page < 4; page++)
        {
            var result = await audit.QueryAsync(null, null, null, null, page * 5, 5, null, null, CancellationToken.None);
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
        var audit = QualityAudit(db);

        var byUser = await audit.QueryAsync(null, null, null, null, 0, 50, "userName", "asc", CancellationToken.None);
        Assert.Equal(byUser.Items.Select(x => x.UserName).OrderBy(x => x, StringComparer.Ordinal),
            byUser.Items.Select(x => x.UserName));

        // 排序键是从前端来的，不能进 SQL：白名单外的值一律回退到默认的时间倒序。
        var injected = await audit.QueryAsync(
            null, null, null, null, 0, 50, "(SELECT 1)--)--", "whatever", CancellationToken.None);
        var latest = await audit.QueryAsync(null, null, null, null, 0, 1, null, null, CancellationToken.None);
        Assert.Equal(latest.Items.Single().Id, injected.Items.First().Id);
        // 越界的 take/skip 一律收敛，不能拼成非法 SQL 或负数偏移。
        Assert.Equal(12, (await audit.QueryAsync(null, null, null, null, -5, 9_000, null, null, CancellationToken.None)).Items.Count);
    }

    /// <summary>
    /// 搜索覆盖全库：命中的行恰好不在第一页时也必须能搜到——只过滤当页 50 条时，
    /// "没搜到"会被读成"没发生过"。同时钉住 % / _ 的通配符转义。
    /// </summary>
    [Fact]
    public async Task SearchesWholeTableByKeywordAndEscapesWildcards()
    {
        await using var db = OpenDb();
        Seed(db, 12);
        // 两行"更旧"的记录：默认时间倒序下它们在最后一页，按当页过滤永远搜不到。
        db.AuditLogs.Add(new AuditLog(null, "zhang", "batch.release.esign", "ProductionBatch", "B1", "偏差放行 检验合格"));
        db.AuditLogs.Add(new AuditLog(null, "li", "audit.plain", "Test", "T9", "含水率 50%_v2"));
        db.SaveChanges();
        var audit = QualityAudit(db);

        var byUser = await audit.QueryAsync(null, null, "zhang", null, 0, 5, null, null, CancellationToken.None);
        Assert.Equal(1, byUser.Total);
        Assert.Equal("zhang", byUser.Items.Single().UserName);

        var byDetail = await audit.QueryAsync(null, null, "放行", null, 0, 50, null, null, CancellationToken.None);
        Assert.Equal(1, byDetail.Total);

        // "%" 不转义会匹配所有行；"_" 不转义会把 "50%_v2" 当成"任意单字符"。
        var literal = await audit.QueryAsync(null, null, "50%_v2", null, 0, 50, null, null, CancellationToken.None);
        Assert.Equal(1, literal.Total);
        var percentOnly = await audit.QueryAsync(null, null, "%", null, 0, 50, null, null, CancellationToken.None);
        Assert.Equal(1, percentOnly.Total);
    }

    /// <summary>
    /// 中文标签在库里不存在（动作列存 code、实体列存英文枚举名），前端 labels.ts 把命中的
    /// code 反查出来走 qTokens 一并送来。没有这一步，"放行 / 生产批次"这类词改到全库检索后会失效。
    /// </summary>
    [Fact]
    public async Task Label_tokens_match_even_when_stored_text_does_not()
    {
        await using var db = OpenDb();
        db.AuditLogs.Add(new AuditLog(null, "qa", "batch.release.esign", "ProductionBatch", "B1", "检验合格"));
        db.AuditLogs.Add(new AuditLog(null, "op", "batch.create", "ProductionBatch", "B2", "换型"));
        db.SaveChanges();
        var audit = QualityAudit(db);

        // 库里没有任何字段含"放行"二字，命中完全靠 qTokens。
        var hit = await audit.QueryAsync(null, null, "放行", "batch.release.esign", 0, 50, null, null, CancellationToken.None);
        Assert.Equal(1, hit.Total);
        Assert.Equal("batch.release.esign", hit.Items.Single().Action);

        // 实体中文名同理：反查结果走 EntityType IN。
        var byEntity = await audit.QueryAsync(null, null, "生产批次", "ProductionBatch", 0, 50, null, null, CancellationToken.None);
        Assert.Equal(2, byEntity.Total);

        // 超长参数不放大成无界 IN：只取前 50 个 token。
        var many = string.Join(',', Enumerable.Range(0, 120).Select(i => $"code.{i}"));
        var bounded = await audit.QueryAsync(null, null, null, many, 0, 50, null, null, CancellationToken.None);
        Assert.Equal(0, bounded.Total);
    }

    /// <summary>审计日志只对质量与管理员开放：服务层自保，车间角色直接 FORBIDDEN。</summary>
    [Fact]
    public async Task Workshop_roles_cannot_read_the_global_audit_log()
    {
        await using var db = OpenDb();
        var audit = new AuditService(db, new ServiceHarness.RoleUser(Guid.NewGuid(), UserRole.Operator, "operator"));

        var denied = await Assert.ThrowsAsync<DomainException>(() =>
            audit.QueryAsync(null, null, null, null, 0, 50, null, null, CancellationToken.None));
        Assert.Equal("FORBIDDEN", denied.Code);
    }

    private static AuditService QualityAudit(AppDbContext db) =>
        new(db, new ServiceHarness.RoleUser(Guid.NewGuid(), UserRole.Quality, "qa"));

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