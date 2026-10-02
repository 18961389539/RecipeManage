using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>配方读路径的直接用例（此前 Compare 的服务封装没有测试，只测了领域层的比较器）。</summary>
public sealed class RecipeQueryServiceTests
{
    private static async Task<MasterRecipe> SeedAsync(RecipesManage.Infrastructure.Persistence.AppDbContext db)
    {
        var recipe = MasterRecipe.Create("Q-1", "query", "P", "part", null, Guid.NewGuid());
        var draft = recipe.RequireDraft();
        draft.ReplaceProcedure(
            [new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                [new RecipeParameter(0, "temp", "℃", 100, 90, 110, true, false)])],
            []);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe;
    }

    [Fact]
    public async Task Get_ReturnsTheRecipeWithItsDraft_AndRefusesUnknownIds()
    {
        await using var db = ServiceHarness.OpenDb("brmes-rq");
        var recipe = await SeedAsync(db);
        var query = ServiceHarness.NewRecipeQuery(db);

        var detail = await query.GetAsync(recipe.Id, CancellationToken.None);
        Assert.Equal("Q-1", detail.Code);
        Assert.Equal("S10", Assert.Single(detail.Draft!.Steps).Code);

        var ex = await Assert.ThrowsAsync<DomainException>(() => query.GetAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal("NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Compare_SameVersion_HasNoChanges_UnknownVersionIsRefused()
    {
        await using var db = ServiceHarness.OpenDb("brmes-rq");
        var recipe = await SeedAsync(db);
        var query = ServiceHarness.NewRecipeQuery(db);

        var same = await query.CompareAsync(recipe.Id, 1, 1, CancellationToken.None);
        Assert.Empty(same.Changes);
        Assert.Empty(same.ChangedStepCodes);

        var ex = await Assert.ThrowsAsync<DomainException>(() => query.CompareAsync(recipe.Id, 1, 9, CancellationToken.None));
        Assert.Equal("NOT_FOUND", ex.Code);
    }
}
