using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 审批链配置的端到端：Admin 存一条链 → 配方选它 → 提交后按链展开 → 各角色依次签。
///
/// 存在的理由：链的**规则**在域层测过了，但"配置真的能被存下来、配方真的会走它"跨了
/// 服务层 + JSON 列 + 角色门三层，任何一层接错，界面表现都是"提交后节点数不对"或"某人永远签不了"。
/// </summary>
public sealed class ApprovalChainConfigTests
{
    private const string Pw = "Cfg@123";

    [Fact]
    public async Task CustomChain_SavesResolvesAndDrivesTheWholeReview()
    {
        await using var db = ServiceHarness.OpenDb("brmes-chaincfg");
        var engineer = await AddUserAsync(db, "cfg-eng", "工艺工程师", UserRole.ProcessEngineer);
        var supervisor = await AddUserAsync(db, "cfg-sup", "主管甲", UserRole.Supervisor);
        var quality = await AddUserAsync(db, "cfg-qa", "质量乙", UserRole.Quality);
        var admin = await AddUserAsync(db, "cfg-adm", "厂长丙", UserRole.Admin);
        var recipeId = await NewDraftRecipeAsync(db, "CFG-1", engineer);
        var chains = Chains(db, admin);

        var saved = await chains.SaveAsync(new SaveApprovalChainRequest(
            null, "plant-release", "三签含放行", IsDefault: false, Enabled: true,
            [
                Step("工艺主管", UserRole.Supervisor),
                Step("质量审核", UserRole.Quality),
                Step("厂长放行", UserRole.Admin)
            ], Pw), CancellationToken.None);

        Assert.Equal(["工艺主管", "质量审核", "厂长放行"], saved.Steps.Select(s => s.Title).ToArray());
        Assert.Equal(
            [UserRole.Supervisor, UserRole.Quality, UserRole.Admin],
            saved.Steps.Select(s => s.RequiredRole).ToArray());

        // 配方选这条链，再提交。
        var recipes = Recipes(db, engineer);
        await recipes.SetApprovalChainAsync(recipeId, new UseApprovalChainRequest("plant-release", Pw), CancellationToken.None);
        var detail = await recipes.SubmitAsync(recipeId, new SubmitRecipeRequest(Pw, "提交"), CancellationToken.None);

        var version = detail.Versions.Single(v => v.Status == RecipeStatus.InReview);
        Assert.Equal(["提交人", "工艺主管", "质量审核", "厂长放行"], version.Approvals.Select(a => a.Title).ToArray());
        Assert.Equal(3, version.Approvals.Count(a => a.Decision == ApprovalDecision.Pending));

        await Recipes(db, supervisor).DecideAsync(recipeId, Decide("路径可执行"), CancellationToken.None);
        await Recipes(db, quality).DecideAsync(recipeId, Decide("窗口可接受"), CancellationToken.None);
        var beforeRelease = await ServiceHarness.NewRecipeQuery(db).GetAsync(recipeId, CancellationToken.None);
        var still = InReview(beforeRelease)!;
        Assert.Equal("厂长放行", still.Approvals.Single(a => a.Decision == ApprovalDecision.Pending).Title);
        Assert.Equal(RecipeStatus.InReview, still.Status);

        var final = await Recipes(db, admin).DecideAsync(recipeId, Decide("可以出厂"), CancellationToken.None);
        Assert.Equal(RecipeStatus.Approved, InReview(final)?.Status ?? RecipeStatus.Approved);
        Assert.NotNull(final.Approved);
        Assert.Equal("工艺工程师|主管甲|质量乙|厂长丙",
            string.Join("|", final.Approved!.Approvals.Select(a => a.ReviewerName)));
    }

    [Fact]
    public async Task SaveRejectsTwoNodesAskingForTheSameRole()
    {
        await using var db = ServiceHarness.OpenDb("brmes-chaincfg");
        var admin = await AddUserAsync(db, "dup-adm", "管理员", UserRole.Admin);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Chains(db, admin).SaveAsync(
            new SaveApprovalChainRequest(null, "dup", "重复角色", false, true,
                [Step("初审", UserRole.Supervisor), Step("复审", UserRole.Supervisor)], Pw),
            CancellationToken.None));

        Assert.Equal("APPROVAL_CHAIN", ex.Code);
        Assert.Contains("Supervisor", ex.Message);
    }

    [Fact]
    public async Task RecipeNamingAMissingChain_IsRefusedNotSilentlyDefaulted()
    {
        await using var db = ServiceHarness.OpenDb("brmes-chaincfg");
        var engineer = await AddUserAsync(db, "miss-eng", "工艺工程师", UserRole.ProcessEngineer);
        var recipeId = await NewDraftRecipeAsync(db, "CFG-2", engineer);

        // 指定一条不存在的链必须当场拒绝：悄悄退回默认链等于悄悄换掉"这版要谁签"。
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            Recipes(db, engineer).SetApprovalChainAsync(recipeId,
                new UseApprovalChainRequest("nope", Pw), CancellationToken.None));
        Assert.Equal("APPROVAL_CHAIN", ex.Code);
    }

    [Fact]
    public async Task DisabledChainSelectedByARecipe_IsRefusedAtSubmitTime()
    {
        await using var db = ServiceHarness.OpenDb("brmes-chaincfg");
        var admin = await AddUserAsync(db, "dis-adm", "管理员", UserRole.Admin);
        var engineer = await AddUserAsync(db, "dis-eng", "工艺工程师", UserRole.ProcessEngineer);
        var chains = Chains(db, admin);

        var created = await chains.SaveAsync(new SaveApprovalChainRequest(
            null, "temp", "临时链", false, true, [Step("质量复核", UserRole.Quality)], Pw), CancellationToken.None);
        var recipeId = await NewDraftRecipeAsync(db, "CFG-3", engineer);
        await Recipes(db, engineer).SetApprovalChainAsync(recipeId, new UseApprovalChainRequest("temp", Pw), CancellationToken.None);

        await chains.SaveAsync(new SaveApprovalChainRequest(
            created.Id, "temp", "临时链", false, false, [Step("质量复核", UserRole.Quality)], Pw), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            Recipes(db, engineer).SubmitAsync(recipeId, new SubmitRecipeRequest(Pw, "提交"), CancellationToken.None));
        Assert.Equal("APPROVAL_CHAIN", ex.Code);
    }

    [Fact]
    public async Task CancellingTheDefaultChain_IsRefused()
    {
        await using var db = ServiceHarness.OpenDb("brmes-chaincfg");
        var admin = await AddUserAsync(db, "def-adm", "管理员", UserRole.Admin);
        var chains = Chains(db, admin);
        var standard = (await chains.ListAsync(CancellationToken.None)).Single(c => c.IsDefault);

        // 没别的链接棒时取消默认 = 之后所有没选链的配方无路可走。
        var ex = await Assert.ThrowsAsync<DomainException>(() => chains.SaveAsync(
            new SaveApprovalChainRequest(standard.Id, standard.Code, standard.Name, false, true,
                standard.Steps.Select(s => Step(s.Title, s.RequiredRole)).ToList(), Pw), CancellationToken.None));
        Assert.Equal("APPROVAL_CHAIN", ex.Code);

        // 把默认让给新链之后，原来那条就能退下来了。
        var successor = await chains.SaveAsync(new SaveApprovalChainRequest(
            null, "newdefault", "新默认", true, true, [Step("质量", UserRole.Quality)], Pw), CancellationToken.None);
        Assert.True(successor.IsDefault);

        var demoted = await chains.SaveAsync(new SaveApprovalChainRequest(
            standard.Id, standard.Code, standard.Name, false, true,
            standard.Steps.Select(s => Step(s.Title, s.RequiredRole)).ToList(), Pw), CancellationToken.None);
        Assert.False(demoted.IsDefault);

        var defaults = (await chains.ListAsync(CancellationToken.None))
            .Where(c => c.IsDefault).Select(c => c.Id).ToArray();
        Assert.Equal(new[] { successor.Id }, defaults);
    }

    // ---- 装配 ----

    private static ApprovalChainStepRequest Step(string title, UserRole role) =>
        new(title, role, $"我确认「{title}」。", $"我驳回「{title}」。");

    private static DecideRequest Decide(string comment) =>
        new(ApprovalDecision.Approved, comment, Pw);

    private static RecipeVersionDto? InReview(RecipeDetailDto detail) =>
        detail.Versions.SingleOrDefault(v => v.Status == RecipeStatus.InReview);

    private static ApprovalChainService Chains(AppDbContext db, AppUser user) =>
        ServiceHarness.NewApprovalChainService(
            db, new ServiceHarness.RoleUser(user.Id, user.Role, user.UserName, user.DisplayName), new BcryptPasswordHasher());

    private static RecipeApprovalService Recipes(AppDbContext db, AppUser user) =>
        ServiceHarness.NewRecipeApproval(
            db, new ServiceHarness.RoleUser(user.Id, user.Role, user.UserName, user.DisplayName), new BcryptPasswordHasher());

    private static async Task<AppUser> AddUserAsync(AppDbContext db, string name, string display, UserRole role)
    {
        var user = new AppUser(name, display, new BcryptPasswordHasher().Hash(Pw), role);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Guid> NewDraftRecipeAsync(AppDbContext db, string code, AppUser engineer)
    {
        var recipe = MasterRecipe.Create(code, "链配置测试", "P", "part", null, engineer.Id);
        var draft = recipe.RequireDraft();
        draft.ReplaceProcedure(
            [new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                [new RecipeParameter(0, "温度", "℃", 530, 520, 540, true, true)])],
            []);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }
}
