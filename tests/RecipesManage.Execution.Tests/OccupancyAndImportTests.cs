using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

using static RecipesManage.Execution.Tests.ServiceHarness;

namespace RecipesManage.Execution.Tests;

public sealed class OccupancyAndImportTests
{
    [Fact]
    public async Task StartAsync_PublishesOccupancyOccupiedBeforeHandshake()
    {
        await using var db = OpenDb();
        var hasher = new BcryptPasswordHasher();
        var operatorUser = new AppUser("operator", "车间操作员", hasher.Hash("Operator@123"), UserRole.Operator);
        db.Users.Add(operatorUser);
        var equipment = new EquipmentLine(
            "HT-OCC", "occupancy furnace", PlcProtocol.Simulator, "127.0.0.1", 102,
            "S7_1200", 0, 1, "{}", "it");
        db.Equipment.Add(equipment);

        var recipe = MasterRecipe.Create("OCC-HT", "occupancy recipe", "P", "part", null, operatorUser.Id);
        var draft = recipe.RequireDraft();
        var s1 = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
            [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true)]);
        draft.ReplaceProcedure([s1], []);
        draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
        draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();

        var events = new ConcurrentBag<ExecutionEvent>();
        var user = new RoleUser(operatorUser.Id, UserRole.Operator, "operator", "车间操作员");
        var batches = new BatchService(
            db,
            user,
            new RecordingScheduler(),
            hasher,
            new NoopPdf(),
            new CapturingPublisher(events),
            new MaterialLotService(db, user, hasher),
            new EquipmentLeaseService(db, NullLogger<EquipmentLeaseService>.Instance));

        var created = await batches.CreateAsync(new CreateBatchRequest(
            "BOCC1", recipe.Id, equipment.Id), CancellationToken.None);
        Assert.DoesNotContain(events, e => e.Type == "occupancy");

        await batches.StartAsync(created.Id, "Operator@123", CancellationToken.None);

        var occ = Assert.Single(events, e => e.Type == "occupancy");
        var rows = Assert.IsAssignableFrom<IReadOnlyList<EquipmentOccupancyDto>>(occ.Payload);
        var row = Assert.Single(rows, r => r.Code == "HT-OCC");
        Assert.Equal("Occupied", row.Occupancy);
        Assert.Equal("BOCC1", row.BatchNo);
        Assert.Equal(created.Id, row.BatchId);
    }

    [Fact]
    public async Task StartAsync_RejectsSecondBatchOnSameEquipment()
    {
        await using var db = OpenDb();
        var hasher = new BcryptPasswordHasher();
        var operatorUser = new AppUser("operator", "车间操作员", hasher.Hash("Operator@123"), UserRole.Operator);
        db.Users.Add(operatorUser);
        var equipment = new EquipmentLine(
            "HT-BUSY", "busy furnace", PlcProtocol.Simulator, "127.0.0.1", 102,
            "S7_1200", 0, 1, "{}", "it");
        db.Equipment.Add(equipment);

        var recipe = MasterRecipe.Create("BUSY-HT", "busy recipe", "P", "part", null, operatorUser.Id);
        var draft = recipe.RequireDraft();
        var s1 = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
            [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true)]);
        draft.ReplaceProcedure([s1], []);
        draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
        draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();

        var user = new RoleUser(operatorUser.Id, UserRole.Operator, "operator", "车间操作员");
        var batches = new BatchService(
            db,
            user,
            new RecordingScheduler(),
            hasher,
            new NoopPdf(),
            new CapturingPublisher(new ConcurrentBag<ExecutionEvent>()),
            new MaterialLotService(db, user, hasher),
            new EquipmentLeaseService(db, NullLogger<EquipmentLeaseService>.Instance));

        var first = await batches.CreateAsync(new CreateBatchRequest("BBUSY1", recipe.Id, equipment.Id), CancellationToken.None);
        var second = await batches.CreateAsync(new CreateBatchRequest("BBUSY2", recipe.Id, equipment.Id), CancellationToken.None);
        await batches.StartAsync(first.Id, "Operator@123", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            batches.StartAsync(second.Id, "Operator@123", CancellationToken.None));
        Assert.Equal("EQ_BUSY", ex.Code);
        Assert.Contains("绑定设备已有批次", ex.Message);
    }

    [Fact]
    public async Task ReconcileAsync_TransfersLeaseFromFaultedToHeld()
    {
        await using var db = OpenDb();
        var hasher = new BcryptPasswordHasher();
        var operatorUser = new AppUser("operator", "车间操作员", hasher.Hash("Operator@123"), UserRole.Operator);
        db.Users.Add(operatorUser);
        var equipment = new EquipmentLine(
            "HT-XFER", "xfer furnace", PlcProtocol.Simulator, "127.0.0.1", 102,
            "S7_1200", 0, 1, "{}", "it");
        db.Equipment.Add(equipment);

        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        var faulted = MakeLive(db, "BFAULT", equipment.Id, operatorUser.Id, t0);
        faulted.Fault("PLC", "超时");
        var held = MakeLive(db, "BHELD", equipment.Id, operatorUser.Id, t0.AddHours(1));
        held.Hold("现场确认");
        db.EquipmentLeases.Add(new EquipmentLease(equipment.Id, equipment.Code, faulted.Id, faulted.BatchNo, t0));
        await db.SaveChangesAsync();

        await new EquipmentLeaseService(db, NullLogger<EquipmentLeaseService>.Instance)
            .ReconcileAsync(CancellationToken.None);

        var lease = Assert.Single(db.EquipmentLeases.AsNoTracking().ToList());
        Assert.Equal(held.Id, lease.BatchId);
        Assert.Equal("BHELD", lease.BatchNo);

        var occ = OccupancyRealtime.Snapshot(
            [equipment],
            db.Batches.AsNoTracking().ToList(),
            db.EquipmentLeases.AsNoTracking().ToList());
        var row = Assert.Single(occ, r => r.Code == "HT-XFER");
        Assert.Equal("Occupied", row.Occupancy);
        Assert.Equal("BHELD", row.BatchNo);
        Assert.Equal(nameof(BatchStatus.Held), row.BatchStatus);
    }

    private static ProductionBatch MakeLive(
        AppDbContext db, string batchNo, Guid equipmentId, Guid userId, DateTimeOffset started)
    {
        var stepId = Guid.NewGuid();
        var snapshot = new ControlRecipeSnapshot
        {
            MasterRecipeId = Guid.NewGuid(),
            RecipeVersionId = Guid.NewGuid(),
            VersionNumber = 1,
            RecipeCode = "T",
            RecipeName = "t",
            ProductCode = "P",
            ProductName = "p",
            FrozenAt = started,
            Steps =
            [
                new SnapshotStep
                {
                    StepId = stepId,
                    Code = "S10",
                    Name = "heat",
                    Type = StepType.Heat,
                    Ordinal = 0,
                    WatchdogSeconds = 30,
                    Parameters = []
                }
            ]
        };
        var json = JsonSerializer.Serialize(snapshot, BatchService.JsonOptions);
        var batch = ProductionBatch.Create(batchNo, equipmentId, snapshot, json, userId);
        batch.StepExecutions.Add(new BatchStepExecution(batch.Id, stepId, "S10", "heat", StepType.Heat, 0));
        batch.Queue();
        batch.MarkRunning(started);
        db.Batches.Add(batch);
        return batch;
    }

    [Fact]
    public async Task ImportAsync_CreatesDraftAndSkipsExistingCode()
    {
        await using var db = OpenDb();
        var hasher = new BcryptPasswordHasher();
        var user = new AppUser("engineer", "工艺工程师", hasher.Hash("Engineer@123"), UserRole.ProcessEngineer);
        db.Users.Add(user);
        var recipe = MasterRecipe.Create("IMP-SRC", "import source", "P", "part", null, user.Id);
        var draft = recipe.RequireDraft();
        var step = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
            [new RecipeParameter(0, "目标温度", "℃", 120, 90, 130, true, true)]);
        draft.ReplaceProcedure([step], []);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();

        var recipes = new RecipeService(db, new RoleUser(user.Id, UserRole.ProcessEngineer, "engineer", "工艺工程师"), hasher);
        var exported = await recipes.ExportAsync(CancellationToken.None);
        var cloneCode = "IMP-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var package = exported with
        {
            Recipes = exported.Recipes.Select(r => r with { Code = cloneCode, Name = "imported clone" }).ToList()
        };

        var first = await recipes.ImportAsync(package, CancellationToken.None);
        Assert.Equal(1, first.Created);
        Assert.Equal(0, first.Skipped);
        Assert.Contains(first.Messages, m => m.Contains("须重新电子签名"));

        var imported = await db.Recipes.AsNoTracking().SingleAsync(r => r.Code == cloneCode);
        Assert.NotNull(imported.CurrentDraftVersionId);
        Assert.Null(imported.CurrentApprovedVersionId);

        var second = await recipes.ImportAsync(package, CancellationToken.None);
        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Skipped);
    }
}
