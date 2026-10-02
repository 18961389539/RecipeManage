using Microsoft.Extensions.Logging.Abstractions;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using RecipesManage.Infrastructure.Plc;
using Xunit;
using RecipesManage.Simulation;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 总览磁贴的数字必须等于"点进去那个列表会有多少条"。
///
/// 为什么用测试而不是共享代码来守：谓词已经收进 <see cref="LabSampleQuery"/>，但配方类计数与
/// 配方列表 / 审核台的筛选之间没有可共享的代码（后者是前端按 draftStatus 过滤的），
/// 于是"磁贴写 3、点进去 2 条"这种错只能靠断言把两边钉在一起。
/// </summary>
public sealed class DashboardCountTests
{
    private static readonly Guid Actor = Guid.NewGuid();

    private static ServiceHarness.RoleUser As(UserRole role) => new(Actor, role, "tester", "测试的人");

    private static EquipmentService Dashboard(AppDbContext db) =>
        new EquipmentService(db, As(UserRole.Admin),
            new PlcDriverFactory([new SimulatedDriverProvider(new SimulatedPlcRack())]), NullLogger<EquipmentService>.Instance);

    private static BatchQueryService Batches(AppDbContext db) =>
        ServiceHarness.NewBatchQuery(db, As(UserRole.Operator),
            ServiceHarness.NewMaterialLotService(db, As(UserRole.Operator), new BcryptPasswordHasher()));

    [Fact]
    public async Task PendingLabTileCountsBatchesSoTheTileMatchesTheListItOpens()
    {
        await using var db = ServiceHarness.OpenDb();
        var now = DateTimeOffset.UtcNow;
        var twoPending = ServiceHarness.Batch("DLAB-A");
        var onePending = ServiceHarness.Batch("DLAB-B");
        db.Batches.AddRange(twoPending, onePending);
        await db.SaveChangesAsync();

        // 一个批次可以挂多个待判终样：数样品会得到 3，数批次才是 2 —— 而磁贴点开的是批次列表。
        db.LabSamples.Add(new LabSample("F1", twoPending.Id, LabSampleType.Final, "qa", now));
        db.LabSamples.Add(new LabSample("F2", twoPending.Id, LabSampleType.Final, "qa", now));
        db.LabSamples.Add(new LabSample("F3", onePending.Id, LabSampleType.Final, "qa", now));
        // 过程样不欠质量判定，不能把批次算进"待检终样"。
        db.LabSamples.Add(new LabSample("IP1", onePending.Id, LabSampleType.InProcess, "qa", now));
        await db.SaveChangesAsync();

        var dash = await Dashboard(db).DashboardAsync(CancellationToken.None);
        var list = await Batches(db).ListAsync(0, 50, null, null, null, null, true, CancellationToken.None);

        Assert.Equal(2, dash.PendingLabBatches);
        Assert.Equal(dash.PendingLabBatches, list.Total);
        Assert.All(list.Items, b => Assert.True(b.PendingFinalSample));
    }

    [Fact]
    public async Task RecipeTilesCountRecipesOnTheSameRelationTheListsFilter()
    {
        await using var db = ServiceHarness.OpenDb();
        var draftOnly = MasterRecipe.Create("DREC-A", "只有草稿", "P", "part", null, Actor);
        var approved = MasterRecipe.Create("DREC-B", "已有生效版本", "P", "part", null, Actor);
        // 用域对象的指针来"批准"而不是改版本状态：配方列表判断有没有生效版本，看的是
        // CurrentApprovedVersionId 能不能解析出版本行。磁贴以前数的是 Status==Approved 的版本行，
        // 两者一旦不一致（导入的配方包、历史回填）就会一个说有 5 个、一个列表里 0 个。
        approved.MarkApproved(approved.RequireDraft());
        db.Recipes.AddRange(draftOnly, approved);
        await db.SaveChangesAsync();

        var dash = await Dashboard(db).DashboardAsync(CancellationToken.None);
        var rows = await ServiceHarness.NewRecipeQuery(db)
            .ListAsync(CancellationToken.None);

        Assert.Equal(rows.Count(r => r.ApprovedVersion is > 0), dash.ApprovedRecipes);
        Assert.Equal(1, dash.ApprovedRecipes);
        Assert.Equal(rows.Count(r => r.DraftStatus == RecipeStatus.Draft), dash.DraftRecipes);
        Assert.Equal(rows.Count(r => r.DraftStatus == RecipeStatus.InReview), dash.PendingApprovals);
        // 2 而不是 1：B 的草稿指针被 MarkApproved 清了，列表按"没有草稿就退回生效版本状态"仍报 Draft。
        // 这条回退就是磁贴以前数错的地方，所以断言直接拿列表 DTO 对账而不是另写一遍规则。
        Assert.Equal(2, dash.DraftRecipes);
        Assert.Equal(0, dash.PendingApprovals);
    }

    [Fact]
    public async Task LiveBatchesIsInFlightNotRunning()
    {
        await using var db = ServiceHarness.OpenDb();
        var now = DateTimeOffset.UtcNow;
        var running = ServiceHarness.Batch("DLVE-R");
        var held = ServiceHarness.Batch("DLVE-H");
        var faulted = ServiceHarness.Batch("DLVE-F");
        var released = ServiceHarness.Batch("DLVE-L");
        running.Queue();
        running.MarkRunning(now);
        held.Queue();
        held.MarkRunning(now);
        held.Hold("演示保持");
        faulted.Queue();
        faulted.MarkRunning(now);
        faulted.Fault("E1", "握手故障");
        released.Queue();
        released.MarkRunning(now);
        released.Complete(now);
        released.Release("qa", "质量放行", now);
        db.Batches.AddRange(running, held, faulted, released);
        await db.SaveChangesAsync();

        var dash = await Dashboard(db).DashboardAsync(CancellationToken.None);

        // 表里是"在途"（运行/排队/保持/故障），磁贴各数各的状态：
        // 标题与空态以前写"实时批次/当前没有执行中的批次"，表里 3 行而磁贴说执行中 1，互相打脸。
        Assert.Equal(3, dash.LiveBatches.Count);
        Assert.Equal(1, dash.RunningBatches);
        Assert.Equal([BatchStatus.Faulted, BatchStatus.Held, BatchStatus.Running],
            dash.LiveBatches.Select(b => b.Status).OrderBy(s => s.ToString(), StringComparer.Ordinal).ToList());
        Assert.DoesNotContain(dash.LiveBatches, b => b.Status == BatchStatus.Released);
    }
}
