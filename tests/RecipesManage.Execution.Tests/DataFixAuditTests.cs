using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 一次性数据修复会替业务改写受控数据（已批准配方的工步、设备的握手点表），
/// 所以每一条改写都必须在审计履历里查得到——改完查不到，就等于说不清改没改过。
/// 草稿不是受控记录，机器改它不该刷履历。
/// </summary>
public sealed class DataFixAuditTests
{
    [Fact]
    public async Task ApplyAsync_HeatDurationBackfill_OnApprovedVersion_WritesAuditOnce()
    {
        await using var db = OpenDb();
        var op = new AppUser("operator", "操作员", "not-a-hash", UserRole.Operator);
        db.Users.Add(op);
        var recipe = RecipeMissingHeatDuration("FX-HEAT", draftOnly: false);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();

        await DataFixRunner.ApplyAsync(db, NullLogger<AppDbContext>.Instance);

        var audit = Assert.Single(db.AuditLogs.Local, a => a.Action == "recipe.autofix.heat-duration");
        Assert.Equal("system", audit.UserName);
        Assert.Equal("RecipeVersion", audit.EntityType);
        Assert.Equal(recipe.Versions.Single().Id.ToString(), audit.EntityId);
        Assert.Contains("S10", audit.Detail);
        Assert.True(await db.DataFixes.AnyAsync(f => f.Key == "heat-duration"));

        // 再跑一次：已登记的修复不得重复改写、更不得重复刷履历。
        await DataFixRunner.ApplyAsync(db, NullLogger<AppDbContext>.Instance);
        Assert.Equal(1, db.AuditLogs.Local.Count(a => a.Action == "recipe.autofix.heat-duration"));
    }

    [Fact]
    public async Task ApplyAsync_HeatDurationBackfill_OnDraft_WritesNoAudit()
    {
        await using var db = OpenDb();
        var op = new AppUser("operator", "操作员", "not-a-hash", UserRole.Operator);
        db.Users.Add(op);
        db.Recipes.Add(RecipeMissingHeatDuration("FX-DRAFT", draftOnly: true));
        await db.SaveChangesAsync();

        await DataFixRunner.ApplyAsync(db, NullLogger<AppDbContext>.Instance);

        Assert.Empty(db.AuditLogs.Local);
        Assert.True(await db.DataFixes.AnyAsync(f => f.Key == "heat-duration"));
    }

    [Fact]
    public async Task ApplyAsync_HoldTags_OnModbusEquipment_WritesAudit()
    {
        await using var db = OpenDb();
        // 点表里没有 Host_Hold：这条修复会补握手地址，而地址写错就是现场误动作。
        db.Equipment.Add(new EquipmentLine(
            "MB-1", "modbus line", PlcProtocol.ModbusTcp, "127.0.0.1", 502, "Modbus", 0, 1, "{}", "it"));
        await db.SaveChangesAsync();

        await DataFixRunner.ApplyAsync(db, NullLogger<AppDbContext>.Instance);

        var audit = Assert.Single(db.AuditLogs.Local, a => a.Action == "equipment.autofix.hold-tags");
        Assert.Contains("MB-1", audit.Detail);
        Assert.Contains("Host_Hold", audit.Detail);
    }

    /// <summary>
    /// Heat 工步只给了设定值、没有时长参数——这正是 heat-duration 那条修复要补的样子
    /// （不补的话仿真会把斜率当工艺秒数用）。
    /// </summary>
    private static MasterRecipe RecipeMissingHeatDuration(string code, bool draftOnly)
    {
        var recipe = MasterRecipe.Create(code, "heat duration fix", "P", "part", null, Guid.NewGuid());
        var draft = recipe.RequireDraft();
        draft.ReplaceProcedure(
            [new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                [new RecipeParameter(0, "设定值", "", 60, 10, 200, true, false)])],
            []);

        if (draftOnly)
            return recipe;

        draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
        draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);
        return recipe;
    }

    private static AppDbContext OpenDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"brmes-datafix-{Guid.NewGuid():N}.db")}")
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
