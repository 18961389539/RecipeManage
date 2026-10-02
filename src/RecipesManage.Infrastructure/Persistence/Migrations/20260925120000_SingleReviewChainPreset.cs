using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RecipesManage.Domain.Recipes;

#nullable disable

namespace RecipesManage.Infrastructure.Persistence.Migrations;

/// <summary>
/// 补一条给两三人小厂用的预设审批链：<c>single-review</c>（提交 + 一道质量签核）。
///
/// 为什么是数据迁移而不是改默认值：默认三级链是按"工艺工程师 / 工艺主管 / 质量"三个岗位三个人写的，
/// 而域层禁止同一个人在一条链上签两次（那是职责分离的全部意义）。2~3 人的厂因此要么天天卡在自己的
/// 审批台上，要么去建一堆共用账号自签自放——后者更糟，它把合规假装成了数字。
/// 这里给的是合规的退路：只保留质量这一道签核，提交人与签核人仍然是两个人，那条规则一行没松。
///
/// <c>IsDefault = FALSE</c>：现有客户的配方继续走三级，谁都不被这条预设推着改流程；
/// 要用的人在「审批链配置」里选它，或者按配方逐条指定。
///
/// 幂等：与 standard 同法用 <c>WHERE NOT EXISTS</c>，重复执行不产生第二行。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260925120000_SingleReviewChainPreset")]
public partial class SingleReviewChainPreset : Migration
{
    private static readonly Guid SingleReviewChainId = new("6b1e52a7-3f9d-4c08-9f4b-5d2a8c6e14b3");

    /// <summary>与 <see cref="ApprovalChain.SingleReview"/> 同一份定义，免得种下去的链与代码里的预设各说各话。</summary>
    private static readonly string StepsJson =
        ApprovalChainConfig.Write(ApprovalChain.SingleReview.Steps).Replace("'", "''");

    private const string SeedAt = "2026-01-01 00:00:00+00:00";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            $"""
             INSERT INTO approval_chains
                 ("Id", "Code", "Name", "StepsJson", "IsDefault", "Enabled", "CreatedAt", "UpdatedAt")
             SELECT '{SingleReviewChainId}', 'single-review', '{ApprovalChain.SingleReview.Name}',
                    '{StepsJson}', FALSE, TRUE, '{SeedAt}', NULL
             WHERE NOT EXISTS (SELECT 1 FROM approval_chains WHERE "Code" = 'single-review');
             """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // 这条 Down 不是产品的回滚路径（全仓没有任何地方调用 Down，回滚 = 恢复升级前那份快照，
        // 见 SchemaBootstrap.ApplyAsync）。它只为 dotnet ef database update 的手工操作留个形式：
        // 如果还有配方选用这条链，删掉会让那些配方的"还差谁签"退回默认链，等于改了别人选定的流程，
        // 所以这里按引用情况删，不硬删。
        migrationBuilder.Sql(
            """
            DELETE FROM approval_chains
             WHERE "Code" = 'single-review'
               AND NOT EXISTS (SELECT 1 FROM master_recipes WHERE "ApprovalChainCode" = 'single-review');
            """);
    }
}
