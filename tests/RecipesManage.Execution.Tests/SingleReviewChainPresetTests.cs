using Microsoft.EntityFrameworkCore;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 小厂预设链（提交 + 一道质量签核）的迁移与形状。
///
/// 这条测试守的是取向而不是 SQL：预设必须<strong>不是</strong>默认链，也不能让同一个人既提交又签。
/// 一个 2~3 人的厂需要的是"少一道签核"，而不是"没有职责分离"——后者一旦被当成便利功能默认打开，
/// 这个产品的全部合规前提就没了。
/// </summary>
public sealed class SingleReviewChainPresetTests
{
    [Fact]
    public async Task Preset_SeedsAsAnEnabledNonDefaultChain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"brmes-shortchain-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
        try
        {
            await using (var db = new AppDbContext(options))
            {
                await SchemaBootstrap.ApplyAsync(db);

                var chains = await db.ApprovalChains.AsNoTracking().ToListAsync();
                var single = chains.SingleOrDefault(c => c.Code == ApprovalChain.SingleReview.Code);
                Assert.NotNull(single);
                Assert.True(single!.Enabled);
                Assert.False(single.IsDefault);                       // 现有客户继续走三级
                Assert.Equal(1, chains.Count(c => c.IsDefault));      // 默认链仍然只有一条
                Assert.Equal(chains.Single(c => c.IsDefault).Code, ApprovalChain.Standard.Code);

                var steps = single.ToChain().Steps;
                Assert.Single(steps);
                Assert.Equal(ApprovalNode.Quality, steps[0].Node);
                Assert.Equal(UserRole.Quality, steps[0].RequiredRole);
                Assert.Null(ApprovalChain.Validate(steps));           // 与界面保存时同一套规则
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* 临时目录 */ }
        }
    }
}
