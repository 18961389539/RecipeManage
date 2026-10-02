using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using static RecipesManage.Execution.Tests.ServiceHarness;

namespace RecipesManage.Execution.Tests;

public sealed class EquipmentPhaseLibraryTests
{
    [Fact]
    public async Task CreateBatch_FurnaceClass_AllowsHeat_RejectsMix()
    {
        await using var db = OpenDb();
        var hasher = new BcryptPasswordHasher();
        var op = new AppUser("operator", "车间操作员", hasher.Hash("Operator@123"), UserRole.Operator);
        db.Users.Add(op);

        var furnace = new EquipmentClass("FURNACE", "热处理炉", null);
        furnace.AddTemplate("PH-HEAT", "升温至设定点", StepType.Heat, null, 180,
        [
            new PhaseParameterSpec
            {
                SlotIndex = 0, Name = "目标温度", EngineeringUnit = "℃", Setpoint = 530,
                Min = 520, Max = 540, WriteToPlc = true, ArchiveAsQuality = true
            }
        ]);
        db.EquipmentClasses.Add(furnace);

        var equipment = new EquipmentLine("HT-CLS", "class furnace", PlcProtocol.Simulator, "127.0.0.1", 102, "S7_1200", 0, 1, "{}", "it");
        equipment.AssignClass("FURNACE");
        db.Equipment.Add(equipment);

        var heat = Approve(MasterRecipe.Create("CLS-HEAT", "heat", "P", "part", null, op.Id), op, StepType.Heat, "heat");
        var mix = Approve(MasterRecipe.Create("CLS-MIX", "mix", "P", "part", null, op.Id), op, StepType.Mix, "mix");
        db.Recipes.AddRange(heat, mix);
        await db.SaveChangesAsync();

        var user = new RoleUser(op.Id, UserRole.Operator, "operator", "车间操作员");
        var batches = ServiceHarness.NewBatchService(db, user, new RecordingScheduler(), hasher, new NoopPdf(), new NoopPublisher(),
            new MaterialLotService(db, user, hasher),
            new EquipmentLeaseService(db, NullLogger<EquipmentLeaseService>.Instance));

        var ok = await batches.CreateAsync(new CreateBatchRequest("BHEAT1", heat.Id, equipment.Id), CancellationToken.None);
        Assert.Equal("Valid", ok.SnapshotIntegrity);
        Assert.Contains(ok.Snapshot.Steps, s => s.Type == StepType.Heat);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            batches.CreateAsync(new CreateBatchRequest("BMIX1", mix.Id, equipment.Id), CancellationToken.None));
        Assert.Equal("EQ_CLASS", ex.Code);
        Assert.Contains("Mix", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateBatch_UnclassifiedEquipment_SkipsCapabilityGate()
    {
        await using var db = OpenDb();
        var hasher = new BcryptPasswordHasher();
        var op = new AppUser("operator", "车间操作员", hasher.Hash("Operator@123"), UserRole.Operator);
        db.Users.Add(op);
        var equipment = new EquipmentLine("HT-NONE", "no class", PlcProtocol.Simulator, "127.0.0.1", 102, "S7_1200", 0, 1, "{}", "it");
        db.Equipment.Add(equipment);
        var mix = Approve(MasterRecipe.Create("NONE-MIX", "mix", "P", "part", null, op.Id), op, StepType.Mix, "mix");
        db.Recipes.Add(mix);
        await db.SaveChangesAsync();

        var user = new RoleUser(op.Id, UserRole.Operator, "operator", "车间操作员");
        var batches = ServiceHarness.NewBatchService(db, user, new RecordingScheduler(), hasher, new NoopPdf(), new NoopPublisher(),
            new MaterialLotService(db, user, hasher),
            new EquipmentLeaseService(db, NullLogger<EquipmentLeaseService>.Instance));
        var created = await batches.CreateAsync(new CreateBatchRequest("BNONE1", mix.Id, equipment.Id), CancellationToken.None);
        Assert.Equal("Valid", created.SnapshotIntegrity);
    }

    [Fact]
    public async Task CreateBatch_QuenchClass_AllowsQualityCheckWithCool()
    {
        await using var db = OpenDb();
        var hasher = new BcryptPasswordHasher();
        var op = new AppUser("operator", "车间操作员", hasher.Hash("Operator@123"), UserRole.Operator);
        db.Users.Add(op);
        var quench = new EquipmentClass("QUENCH", "淬火槽", null);
        quench.AddTemplate("PH-QUENCH", "淬火冷却", StepType.Cool, null, 90,
        [
            new PhaseParameterSpec
            {
                SlotIndex = 0, Name = "终点温度", EngineeringUnit = "℃", Setpoint = 40,
                WriteToPlc = true, ArchiveAsQuality = true
            }
        ]);
        db.EquipmentClasses.Add(quench);
        var equipment = new EquipmentLine("HT-Q", "quench", PlcProtocol.Simulator, "127.0.0.1", 102, "S7_1200", 0, 1, "{}", "it");
        equipment.AssignClass("QUENCH");
        db.Equipment.Add(equipment);

        var recipe = MasterRecipe.Create("Q-COOL", "cool+qc", "P", "part", null, op.Id);
        var draft = recipe.RequireDraft();
        var s1 = new RecipeStep(draft.Id, "S10", "cool", StepType.Cool, 0, 0, 0, 30, null,
            [new RecipeParameter(0, "终点温度", "℃", 40, 20, 60, true, true)]);
        var s2 = new RecipeStep(draft.Id, "S20", "qc", StepType.QualityCheck, 1, 0, 0, 30, null,
            [new RecipeParameter(0, "硬度", "HB", 95, 90, 110, false, false)]);
        draft.ReplaceProcedure([s1, s2], [new RecipeEdge(draft.Id, s1.Id, s2.Id)]);
        draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
        draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();

        var user = new RoleUser(op.Id, UserRole.Operator, "operator", "车间操作员");
        var batches = ServiceHarness.NewBatchService(db, user, new RecordingScheduler(), hasher, new NoopPdf(), new NoopPublisher(),
            new MaterialLotService(db, user, hasher),
            new EquipmentLeaseService(db, NullLogger<EquipmentLeaseService>.Instance));
        var created = await batches.CreateAsync(new CreateBatchRequest("BQ1", recipe.Id, equipment.Id), CancellationToken.None);
        Assert.Equal("Valid", created.SnapshotIntegrity);
    }

    private static MasterRecipe Approve(MasterRecipe recipe, AppUser op, StepType type, string name)
    {
        var draft = recipe.RequireDraft();
        var s1 = new RecipeStep(draft.Id, "S10", name, type, 0, 0, 0, 30, null,
            [new RecipeParameter(0, "设定值", "", 1, 0, 10, true, false)]);
        draft.ReplaceProcedure([s1], []);
        draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
        draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);
        return recipe;
    }
}
