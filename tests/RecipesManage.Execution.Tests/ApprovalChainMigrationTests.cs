using Microsoft.EntityFrameworkCore;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 审批链迁移的**历史行回填**。
///
/// 这条测试的存在理由：库是空的时候迁移永远不会错——approval_records 里的 Level
/// 是这次改动唯一一处"已经发生过的fact"，而 fresh 库上跑迁移的既有用例一行历史数据都没有，
/// 完全测不到回填。实测就把 RequiredRole 写错过（按 0=ProcessEngineer 猜，实际 0=Admin），
/// 结果是每条历史待签节点都要求错角色，谁来了都是 FORBIDDEN。
/// </summary>
public sealed class ApprovalChainMigrationTests
{
    private const string OldSchema = "20260922180000_ParameterSemantics";

    [Fact]
    public async Task Migrate_BackfillsHistoricalLevelRows_IntoFrozenChainNodes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"brmes-chainmig-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;

        // 1) 只迁到改动前一刻的库形状，然后按旧口径（Level）塞历史行。
        await using (var old = new AppDbContext(options))
        {
            await old.Database.MigrateAsync(OldSchema, CancellationToken.None);
            await SeedLegacyRowsAsync(old);
        }

        // 2) 迁到最新：Level 变成冻结副本 + 一条默认链。
        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync(CancellationToken.None);

            var rows = await db.Database.SqlQueryRaw<Backfill>(
                """
                SELECT Seq, Node, RequiredRole, Title, Decision, MeaningApproved, MeaningRejected
                FROM approval_records
                """).ToListAsync();

            Assert.Equal(3, rows.Count);
            Assert.Equal([0, 1, 2], rows.Select(r => r.Seq).Order());

            var submission = rows.Single(r => r.Seq == 0);
            Assert.Equal((int)ApprovalNode.Submission, submission.Node);
            Assert.Equal((int)UserRole.ProcessEngineer, submission.RequiredRole);
            Assert.Equal(ApprovalChain.SubmissionTitle, submission.Title);
            Assert.Equal(ApprovalChain.Submission.MeaningApproved, submission.MeaningApproved);
            Assert.Equal((int)ApprovalDecision.Approved, submission.Decision);

            var supervisor = rows.Single(r => r.Seq == 1);
            Assert.Equal((int)ApprovalNode.Supervisor, supervisor.Node);
            Assert.Equal((int)UserRole.Supervisor, supervisor.RequiredRole);
            Assert.Equal("工艺主管", supervisor.Title);
            // 旧表里这一行是"待主管签"，回填后仍然是未决——在审版本不能被迁移悄悄签掉。
            Assert.Equal((int)ApprovalDecision.Pending, supervisor.Decision);
            Assert.Equal(ApprovalChain.Standard.Steps[0].MeaningApproved, supervisor.MeaningApproved);
            Assert.Equal(ApprovalChain.Standard.Steps[0].MeaningRejected, supervisor.MeaningRejected);

            var quality = rows.Single(r => r.Seq == 2);
            Assert.Equal((int)UserRole.Quality, quality.RequiredRole);
            Assert.Equal("质量审核", quality.Title);
            Assert.Equal((int)ApprovalDecision.Rejected, quality.Decision);

            // Level 必须真的没了：留着它就有人日后拿它当真源。
            Assert.False(await HasColumnAsync(db, "approval_records", "Level"));

            var chains = await db.ApprovalChains.AsNoTracking().ToListAsync();
            var standard = Assert.Single(chains);
            Assert.Equal("standard", standard.Code);
            Assert.True(standard.IsDefault);
            Assert.Equal(ApprovalChain.Standard.Steps, standard.ToChain().Steps);
        }
    }

    private static async Task SeedLegacyRowsAsync(AppDbContext db)
    {
        var versionId = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO master_recipes ("Id","Code","Name","ProductCode","ProductName","Lifecycle","CreatedAt")
            VALUES ('bg-owner','BG-1','bg','P','part',0,'2026-09-01 00:00:00+00:00');
            """);
        await db.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO recipe_versions ("Id","MasterRecipeId","VersionNumber","Status","CreatedBy","CreatedAt")
            SELECT '{versionId}', Id, 1, 1, 'bg-owner', '2026-09-01 00:00:00+00:00'
            FROM master_recipes WHERE Code = 'BG-1';
            """);

        // 三条历史：提交人已签、主管待签、质量已驳回。Level 是旧口径（Author=0 / Supervisor=1 / Quality=2）。
        foreach (var (level, decision) in new[] { (0, 1), (1, 0), (2, 2) })
        {
            await db.Database.ExecuteSqlRawAsync(
                $"""
                INSERT INTO approval_records ("Id","RecipeVersionId","Level","Decision","CreatedAt")
                VALUES ('{Guid.NewGuid()}', '{versionId}', {level}, {decision}, '2026-09-01 00:00:00+00:00');
                """);
        }
    }

    private static async Task<bool> HasColumnAsync(AppDbContext db, string table, string column)
    {
        var found = await db.Database.SqlQueryRaw<ColumnName>(
            $"""
            SELECT COUNT(*) AS N FROM pragma_table_info('{table}') WHERE name = '{column}'
            """).FirstAsync();
        return found.N > 0;
    }

    private sealed record Backfill(
        int Seq, int Node, int RequiredRole, string Title, int Decision, string MeaningApproved, string MeaningRejected);

    private sealed record ColumnName(int N);
}
