using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 审批链的行为基线。
///
/// 断言刻意只走 RecipeService 的公开入口（SubmitAsync / DecideAsync / ListAsync），不碰域层签名——
/// "审批链数据化"要把 ApprovalLevel 枚举换掉，域层调用点必然跟着改，
/// 但**链推进的语义一条都不许变**，所以钉子钉在服务层这一侧。
/// 默认链 = 现行的 提交 → 工艺主管 → 质量，因此改前改后这些用例必须同样跑绿。
/// </summary>
public sealed class ApprovalChainFlowTests
{
    private const string Pw = "Chain@123";

    [Fact]
    public async Task Submit_HeadNodeIsSupervisor_AndQualityCannotSignIt()
    {
        await using var db = ServiceHarness.OpenDb("brmes-chain");
        var fx = await BuildAsync(db, "CH-1");

        var detail = await Service(db, fx.Engineer)
            .SubmitAsync(fx.RecipeId, new SubmitRecipeRequest(Pw, "提交审核"), CancellationToken.None);

        var review = InReview(detail)!;
        Assert.Contains("工艺主管", Head(review).Meaning);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            Service(db, fx.Quality).DecideAsync(fx.RecipeId,
                new DecideRequest(ApprovalDecision.Approved, "不该轮到我", Pw), CancellationToken.None));
        Assert.Equal("FORBIDDEN", ex.Code);
    }

    [Fact]
    public async Task SupervisorThenQuality_ApprovesVersionAndActivatesRecipe()
    {
        await using var db = ServiceHarness.OpenDb("brmes-chain");
        var fx = await BuildAsync(db, "CH-2");
        var engineer = Service(db, fx.Engineer);
        await engineer.SubmitAsync(fx.RecipeId, new SubmitRecipeRequest(Pw, "提交"), CancellationToken.None);

        await Service(db, fx.Supervisor).DecideAsync(fx.RecipeId,
            new DecideRequest(ApprovalDecision.Approved, "路径可执行", Pw), CancellationToken.None);
        var reviewing = InReview(await engineer.GetAsync(fx.RecipeId, CancellationToken.None))!;
        Assert.Contains("质量", Head(reviewing).Meaning);

        var final = await Service(db, fx.Quality).DecideAsync(fx.RecipeId,
            new DecideRequest(ApprovalDecision.Approved, "窗口可接受", Pw), CancellationToken.None);

        var approved = final.Versions.Single(v => v.Status == RecipeStatus.Approved);
        Assert.NotNull(final.Approved);
        Assert.Equal(1, approved.VersionNumber);
        Assert.NotNull(approved.ApprovedAt);
        // 三个节点各留一个签名人名，顺序就是链顺序。
        Assert.Equal("工艺工程师|主管甲|质量乙",
            string.Join("|", Ordered(approved).Select(a => a.ReviewerName)));
    }

    [Fact]
    public async Task RejectionEndsReview_NoNodeLeftToDecide()
    {
        await using var db = ServiceHarness.OpenDb("brmes-chain");
        var fx = await BuildAsync(db, "CH-3");
        await Service(db, fx.Engineer)
            .SubmitAsync(fx.RecipeId, new SubmitRecipeRequest(Pw, "提交"), CancellationToken.None);

        var rejected = await Service(db, fx.Supervisor).DecideAsync(fx.RecipeId,
            new DecideRequest(ApprovalDecision.Rejected, "路径不可执行", Pw), CancellationToken.None);

        Assert.Equal(RecipeStatus.Rejected, rejected.Versions.Single(v => v.Status == RecipeStatus.Rejected).Status);
        Assert.Null(rejected.Approved);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            Service(db, fx.Quality).DecideAsync(fx.RecipeId,
                new DecideRequest(ApprovalDecision.Approved, "晚了一步", Pw), CancellationToken.None));
        Assert.Equal("NOT_IN_REVIEW", ex.Code);
    }

    [Fact]
    public async Task Submitter_CannotSignHisOwnVersion_EvenWithAnotherRole()
    {
        await using var db = ServiceHarness.OpenDb("brmes-chain");
        var fx = await BuildAsync(db, "CH-4");
        await Service(db, fx.Engineer)
            .SubmitAsync(fx.RecipeId, new SubmitRecipeRequest(Pw, "提交"), CancellationToken.None);

        // 职责分离是域层守卫：这里拿提交人的 UserId 披上主管角色进来，
        // 服务层的角色门会放行，必须靠域层那条"同一版本不得两人以上署名"拦下。
        var asSupervisor = new RecipeService(
            db, new ServiceHarness.RoleUser(fx.Engineer.Id, UserRole.Supervisor, "CH-4-eng", "主管甲"),
            new BcryptPasswordHasher());
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            asSupervisor.DecideAsync(fx.RecipeId,
                new DecideRequest(ApprovalDecision.Approved, "我自己签", Pw), CancellationToken.None));
        Assert.Equal("SEGREGATION_OF_DUTIES", ex.Code);
    }

    [Fact]
    public async Task RoleGateFollowsTheHead_NotAFixedLevel()
    {
        await using var db = ServiceHarness.OpenDb("brmes-chain");
        var fx = await BuildAsync(db, "CH-5");
        await Service(db, fx.Engineer)
            .SubmitAsync(fx.RecipeId, new SubmitRecipeRequest(Pw, "提交"), CancellationToken.None);

        var supervisor = Service(db, fx.Supervisor);
        await supervisor.DecideAsync(fx.RecipeId,
            new DecideRequest(ApprovalDecision.Approved, "路径可执行", Pw), CancellationToken.None);

        // 主管签完之后节点已推进，主管再来一次必须被拒——若授权写成"认死某个级别"就会放过。
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            supervisor.DecideAsync(fx.RecipeId,
                new DecideRequest(ApprovalDecision.Approved, "再签一次", Pw), CancellationToken.None));
        Assert.Equal("FORBIDDEN", ex.Code);
    }

    [Fact]
    public async Task ListSurfacesOnlyTheHeadNode_AsPending()
    {
        await using var db = ServiceHarness.OpenDb("brmes-chain");
        var fx = await BuildAsync(db, "CH-6");
        var engineer = Service(db, fx.Engineer);
        await engineer.SubmitAsync(fx.RecipeId, new SubmitRecipeRequest(Pw, "提交"), CancellationToken.None);

        var row = Assert.Single(await engineer.ListAsync(CancellationToken.None), r => r.Code == "CH-6");
        Assert.Equal(RecipeStatus.InReview, row.DraftStatus);
        Assert.Contains("工艺主管", row.PendingMeaning);

        // 主管签完，列表的待审节点必须换成质量：还报主管会把审核台往已处理的节点上引。
        await Service(db, fx.Supervisor).DecideAsync(fx.RecipeId,
            new DecideRequest(ApprovalDecision.Approved, "可执行", Pw), CancellationToken.None);
        var after = Assert.Single(await engineer.ListAsync(CancellationToken.None), r => r.Code == "CH-6");
        Assert.Contains("质量", after.PendingMeaning);
    }

    // ---- 装配 ----

    private sealed record Fixture(
        Guid RecipeId, AppUser Engineer, AppUser Supervisor, AppUser Quality);

    private static async Task<Fixture> BuildAsync(AppDbContext db, string code)
    {
        var hasher = new BcryptPasswordHasher();
        var engineer = new AppUser($"{code}-eng", "工艺工程师", hasher.Hash(Pw), UserRole.ProcessEngineer);
        var supervisor = new AppUser($"{code}-sup", "主管甲", hasher.Hash(Pw), UserRole.Supervisor);
        var quality = new AppUser($"{code}-qa", "质量乙", hasher.Hash(Pw), UserRole.Quality);
        db.Users.AddRange(engineer, supervisor, quality);

        var recipe = MasterRecipe.Create(code, "审批链基线", "P", "part", null, engineer.Id);
        var draft = recipe.RequireDraft();
        var heat = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
            [new RecipeParameter(0, "温度", "℃", 530, 520, 540, true, true)]);
        var hold = new RecipeStep(draft.Id, "S20", "hold", StepType.Hold, 1, 0, 0, 30, null,
            [new RecipeParameter(0, "保温时长", "s", 8, 1, 60, true, false)]);
        draft.ReplaceProcedure([heat, hold], [new RecipeEdge(draft.Id, heat.Id, hold.Id)]);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return new Fixture(recipe.Id, engineer, supervisor, quality);
    }

    private static RecipeService Service(AppDbContext db, AppUser user) =>
        new(db, new ServiceHarness.RoleUser(user.Id, user.Role, user.UserName, user.DisplayName),
            new BcryptPasswordHasher());

    private static RecipeVersionDto? InReview(RecipeDetailDto detail) =>
        detail.Versions.SingleOrDefault(v => v.Status == RecipeStatus.InReview);

    /// <summary>链上第一个未决节点。推进判据只看顺序，不看它叫什么名字。</summary>
    private static ApprovalDto Head(RecipeVersionDto version) =>
        Ordered(version).First(a => a.Decision == ApprovalDecision.Pending);

    private static IEnumerable<ApprovalDto> Ordered(RecipeVersionDto version) =>
        version.Approvals.OrderBy(a => a.Seq);
}
