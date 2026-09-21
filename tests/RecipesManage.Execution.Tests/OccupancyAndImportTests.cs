using System.Collections.Concurrent;
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
        draft.Submit(DateTimeOffset.UtcNow);
        draft.Decide(ApprovalLevel.Supervisor, Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(ApprovalLevel.Quality, Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();

        var events = new ConcurrentBag<ExecutionEvent>();
        var user = new OperatorUser(operatorUser.Id);
        var batches = new BatchService(
            db,
            user,
            new NoopScheduler(),
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
        draft.Submit(DateTimeOffset.UtcNow);
        draft.Decide(ApprovalLevel.Supervisor, Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(ApprovalLevel.Quality, Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();

        var user = new OperatorUser(operatorUser.Id);
        var batches = new BatchService(
            db,
            user,
            new NoopScheduler(),
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

        var recipes = new RecipeService(db, new EngineerUser(user.Id), hasher);
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

    private static AppDbContext OpenDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"brmes-occ-{Guid.NewGuid():N}.db")}")
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private sealed class NoopScheduler : IBatchScheduler
    {
        public ValueTask EnqueueStartAsync(Guid batchId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueAbortAsync(Guid batchId, string reason, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueHoldAsync(Guid batchId, string reason, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueSkipAsync(Guid batchId, string reason, Guid? stepId = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueConfirmAsync(Guid batchId, string comment, Guid? stepId = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class NoopPdf : IBatchRecordPdf
    {
        public byte[] Render(BatchRecordDto record) => [];
    }

    private sealed class CapturingPublisher(ConcurrentBag<ExecutionEvent> sink) : IExecutionPublisher
    {
        public Task PublishAsync(ExecutionEvent evt, CancellationToken cancellationToken = default)
        {
            sink.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class OperatorUser(Guid id) : ICurrentUser
    {
        public Guid? UserId { get; } = id;
        public string UserName => "operator";
        public string DisplayName => "车间操作员";
        public UserRole? Role => UserRole.Operator;
        public bool IsAuthenticated => true;
    }

    private sealed class EngineerUser(Guid id) : ICurrentUser
    {
        public Guid? UserId { get; } = id;
        public string UserName => "engineer";
        public string DisplayName => "工艺工程师";
        public UserRole? Role => UserRole.ProcessEngineer;
        public bool IsAuthenticated => true;
    }
}
