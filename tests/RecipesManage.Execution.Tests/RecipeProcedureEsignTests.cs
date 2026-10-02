using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

public sealed class RecipeProcedureEsignTests
{
    [Fact]
    public async Task SaveProcedure_RejectsMissingPassword()
    {
        await using var db = ServiceHarness.OpenDb("brmes-esign");
        var recipes = ServiceHarness.NewRecipeService(db, new ServiceHarness.RoleUser(Guid.NewGuid(), UserRole.ProcessEngineer), new BcryptPasswordHasher());
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            recipes.SaveProcedureAsync(Guid.NewGuid(), new SaveProcedureRequest([], [], "", "reason"), CancellationToken.None));
        Assert.Equal("ESIGN", ex.Code);
    }

    [Fact]
    public async Task SaveProcedure_RejectsMissingChangeReason()
    {
        await using var db = ServiceHarness.OpenDb("brmes-esign");
        var hasher = new BcryptPasswordHasher();
        var user = new AppUser("engineer", "工艺工程师", hasher.Hash("Engineer@123"), UserRole.ProcessEngineer);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var recipes = ServiceHarness.NewRecipeService(db, new ServiceHarness.RoleUser(user.Id, UserRole.ProcessEngineer, "engineer", "工艺工程师"), hasher);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            recipes.SaveProcedureAsync(Guid.NewGuid(), new SaveProcedureRequest([], [], "Engineer@123", "  "), CancellationToken.None));
        Assert.Equal("CHANGE_REASON", ex.Code);
    }

    [Fact]
    public async Task SaveProcedure_WithEsign_PersistsAndAudits()
    {
        await using var db = ServiceHarness.OpenDb("brmes-esign");
        var hasher = new BcryptPasswordHasher();
        var user = new AppUser("engineer", "工艺工程师", hasher.Hash("Engineer@123"), UserRole.ProcessEngineer);
        db.Users.Add(user);
        var recipe = MasterRecipe.Create("ES-1", "esign save", "P", "part", null, user.Id);
        var draft = recipe.RequireDraft();
        var step = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
            [new RecipeParameter(0, "temp", "℃", 100, 90, 110, true, false)]);
        draft.ReplaceProcedure([step], []);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();

        var recipes = ServiceHarness.NewRecipeService(db, new ServiceHarness.RoleUser(user.Id, UserRole.ProcessEngineer, "engineer", "工艺工程师"), hasher);
        var saved = await recipes.SaveProcedureAsync(recipe.Id, new SaveProcedureRequest(
            [new SaveStepRequest(step.Id, "S10", "heat", StepType.Heat, 0, 10, 20, 30, "note", "UP-A", "OP-Heat 升温",
                [new SaveParameterRequest(0, "temp", "℃", 120, 90, 130, true, false)])],
            [],
            "Engineer@123",
            "调整升温设定"), CancellationToken.None);

        Assert.Equal(120, saved.Draft!.Steps[0].Parameters[0].Setpoint);
        Assert.Contains(db.AuditLogs, a => a.Action == "recipe.procedure.esign" && a.Detail != null && a.Detail.Contains("调整升温设定"));
    }

    [Fact]
    public async Task ListAsync_SurfacesPendingSupervisorNode()
    {
        await using var db = ServiceHarness.OpenDb("brmes-esign");
        var hasher = new BcryptPasswordHasher();
        var user = new AppUser("engineer", "工艺工程师", hasher.Hash("Engineer@123"), UserRole.ProcessEngineer);
        db.Users.Add(user);
        var recipe = MasterRecipe.Create("AL-RV", "review", "P", "part", null, user.Id);
        var draft = recipe.RequireDraft();
        var s1 = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
            [new RecipeParameter(0, "温度", "℃", 530, 520, 540, true, true)]);
        var s2 = new RecipeStep(draft.Id, "S20", "hold", StepType.Hold, 1, 120, 0, 30, null,
            [new RecipeParameter(0, "保温时长", "s", 8, 1, 60, true, false)]);
        draft.ReplaceProcedure([s1, s2], [new RecipeEdge(draft.Id, s1.Id, s2.Id)]);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();

        var recipes = ServiceHarness.NewRecipeService(db, new ServiceHarness.RoleUser(user.Id, UserRole.ProcessEngineer, "engineer", "工艺工程师"), hasher);
        await recipes.SubmitAsync(recipe.Id, new SubmitRecipeRequest("Engineer@123", "提交审核"), CancellationToken.None);

        var row = Assert.Single(await recipes.ListAsync(CancellationToken.None), r => r.Code == "AL-RV");
        Assert.Equal(RecipeStatus.InReview, row.DraftStatus);
        Assert.Equal("工艺主管", row.PendingTitle);
        Assert.Equal(1, row.ReviewVersion);
        Assert.Contains("工艺主管", row.PendingMeaning);
        Assert.NotEmpty(row.UnitProcedures);
    }
}
