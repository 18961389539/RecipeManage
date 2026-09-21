using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Materials;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

public sealed class MaterialGenealogyIntegrationTests
{
    [Fact]
    public async Task CreateBatch_BindsChargeAndProducedLots_ReleaseBlockedUntilFinalSamplePass()
    {
        await using var db = OpenDb();
        var hasher = new BcryptPasswordHasher();
        var op = new AppUser("operator", "车间操作员", hasher.Hash("Operator@123"), UserRole.Operator);
        var qa = new AppUser("qa", "质量工程师", hasher.Hash("Quality@123"), UserRole.Quality);
        db.Users.AddRange(op, qa);
        var equipment = new EquipmentLine("HT-LOT", "lot furnace", PlcProtocol.Simulator, "127.0.0.1", 102, "S7_1200", 0, 1, "{}", "it");
        db.Equipment.Add(equipment);
        var recipe = MasterRecipe.Create("LOT-HT", "lot recipe", "AL6061", "锻件", null, op.Id);
        var draft = recipe.RequireDraft();
        var s1 = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
            [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true)]);
        draft.ReplaceProcedure([s1], []);
        draft.Submit(DateTimeOffset.UtcNow);
        draft.Decide(ApprovalLevel.Supervisor, op.Id, "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(ApprovalLevel.Quality, qa.Id, "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();

        var opUser = new RoleUser(op.Id, UserRole.Operator, "operator", "车间操作员");
        var qaUser = new RoleUser(qa.Id, UserRole.Quality, "qa", "质量工程师");
        var opLots = new MaterialLotService(db, opUser, hasher);
        var qaLots = new MaterialLotService(db, qaUser, hasher);
        var charge = await opLots.CreateReceivedAsync(new CreateMaterialLotRequest("INGOT-IT-01", "AL6061", "铝锭", 80, "kg"), CancellationToken.None);
        var child = await opLots.SplitAsync(charge.Id, new SplitLotRequest("INGOT-IT-01-S1", 30), CancellationToken.None);
        Assert.Equal(MaterialLotSource.Split, child.Source);

        var batches = new BatchService(db, opUser, new NoopScheduler(), hasher, new NoopPdf(), new NoopPublisher(), opLots, new EquipmentLeaseService(db, NullLogger<EquipmentLeaseService>.Instance));
        var created = await batches.CreateAsync(new CreateBatchRequest(
            "BLOT1", recipe.Id, equipment.Id, 1, "PROD-IT-01", null, [child.Id]), CancellationToken.None);
        Assert.Equal("Valid", created.SnapshotIntegrity);
        Assert.Equal("PROD-IT-01", created.Snapshot.LotNumber);

        var live = await db.Batches.FirstAsync(b => b.Id == created.Id);
        live.Complete(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var sample = await opLots.CreateSampleAsync(created.Id, new CreateLabSampleRequest("QC-IT-1", LabSampleType.Final, child.Id), CancellationToken.None);
        var qaBatches = new BatchService(db, qaUser, new NoopScheduler(), hasher, new NoopPdf(), new NoopPublisher(), qaLots, new EquipmentLeaseService(db, NullLogger<EquipmentLeaseService>.Instance));
        var pending = await Assert.ThrowsAsync<DomainException>(() =>
            qaBatches.ReleaseAsync(created.Id, "放行", "Quality@123", CancellationToken.None));
        Assert.Equal("LAB_PENDING", pending.Code);

        await qaLots.DisposeSampleAsync(sample.Id, new LabSampleDispositionRequest("Quality@123", LabSampleDisposition.Pass, "硬度合格"), CancellationToken.None, created.Id);
        var released = await qaBatches.ReleaseAsync(created.Id, "对照样品放行", "Quality@123", CancellationToken.None);
        Assert.Equal(BatchStatus.Released, released.Status);

        var genealogy = await opLots.GenealogyAsync(charge.Id, CancellationToken.None);
        Assert.Contains(genealogy.Descendants, d => d.LotNumber == "INGOT-IT-01-S1");
        Assert.Contains(genealogy.Uses, u => u.BatchNo == "BLOT1" && u.Role == MaterialUseRole.Charge);

        var record = await qaBatches.RecordAsync(created.Id, CancellationToken.None);
        Assert.Contains(record.Materials!, m => m.Role == MaterialUseRole.Produced && m.LotNumber == "PROD-IT-01");
        Assert.Contains(record.LabSamples!, s => s.SampleCode == "QC-IT-1" && s.Disposition == LabSampleDisposition.Pass);
        var produced = await db.MaterialLots.SingleAsync(l => l.LotNumber == "PROD-IT-01");
        Assert.Equal(MaterialLotStatus.Released, produced.Status);
        var charged = await db.MaterialLots.SingleAsync(l => l.LotNumber == "INGOT-IT-01-S1");
        Assert.Equal(MaterialLotStatus.Consumed, charged.Status);
    }

    private static AppDbContext OpenDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"brmes-lot-{Guid.NewGuid():N}.db")}")
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private sealed class RoleUser(Guid id, UserRole role, string userName, string displayName) : ICurrentUser
    {
        public Guid? UserId { get; } = id;
        public string UserName { get; } = userName;
        public string DisplayName { get; } = displayName;
        public UserRole? Role { get; } = role;
        public bool IsAuthenticated => true;
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
        public byte[] Render(BatchRecordDto record) => [0x25, 0x50, 0x44, 0x46];
    }

    private sealed class NoopPublisher : IExecutionPublisher
    {
        public Task PublishAsync(ExecutionEvent evt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
