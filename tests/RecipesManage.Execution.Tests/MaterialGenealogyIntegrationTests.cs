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

using static RecipesManage.Execution.Tests.ServiceHarness;

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
        draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
        draft.Decide(op.Id, "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(qa.Id, "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();

        var opUser = new RoleUser(op.Id, UserRole.Operator, "operator", "车间操作员");
        var qaUser = new RoleUser(qa.Id, UserRole.Quality, "qa", "质量工程师");
        var opLots = ServiceHarness.NewMaterialLotService(db, opUser, hasher);
        var qaLots = ServiceHarness.NewMaterialLotService(db, qaUser, hasher);
        var qaQuery = ServiceHarness.NewBatchQuery(db, qaUser, qaLots);
        var charge = await opLots.CreateReceivedAsync(new CreateMaterialLotRequest("INGOT-IT-01", "AL6061", "铝锭", 80, "kg"), CancellationToken.None);
        var child = await opLots.SplitAsync(charge.Id, new SplitLotRequest("INGOT-IT-01-S1", 30), CancellationToken.None);
        Assert.Equal(MaterialLotSource.Split, child.Source);

        var batches = ServiceHarness.NewBatchService(db, opUser, new RecordingScheduler(), hasher, new NoopPdf(), new NoopPublisher(), opLots, new EquipmentLeaseService(db, NullLogger<EquipmentLeaseService>.Instance));
        var created = await batches.CreateAsync(new CreateBatchRequest(
            "BLOT1", recipe.Id, equipment.Id, 1, "PROD-IT-01", null, [child.Id]), CancellationToken.None);
        Assert.Equal("Valid", created.SnapshotIntegrity);
        Assert.Equal("PROD-IT-01", created.Snapshot.LotNumber);

        var live = await db.Batches.FirstAsync(b => b.Id == created.Id);
        live.Complete(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var sample = await opLots.CreateSampleAsync(created.Id, new CreateLabSampleRequest("QC-IT-1", LabSampleType.Final, child.Id), CancellationToken.None);
        var qaBatches = ServiceHarness.NewBatchService(db, qaUser, new RecordingScheduler(), hasher, new NoopPdf(), new NoopPublisher(), qaLots, new EquipmentLeaseService(db, NullLogger<EquipmentLeaseService>.Instance));
        var pendingEvidence = await qaQuery.RecordAsync(created.Id, CancellationToken.None);
        var pending = await Assert.ThrowsAsync<DomainException>(() =>
            qaBatches.ReleaseAsync(created.Id, "放行", "Quality@123",
                pendingEvidence.EvidenceHashVersion, pendingEvidence.EvidenceHash, CancellationToken.None));
        Assert.Equal("LAB_PENDING", pending.Code);

        await qaLots.DisposeSampleAsync(sample.Id, new LabSampleDispositionRequest("Quality@123", LabSampleDisposition.Pass, "硬度合格"), CancellationToken.None, created.Id);
        var staleEvidence = await qaQuery.RecordAsync(created.Id, CancellationToken.None);
        db.ProcessSamples.Add(new ProcessSample(
            created.Id, null, DateTimeOffset.UtcNow, "QUALITY_RECHECK", 1, "pass"));
        await db.SaveChangesAsync();
        var stale = await Assert.ThrowsAsync<DomainException>(() =>
            qaBatches.ReleaseAsync(created.Id, "对照样品放行", "Quality@123",
                staleEvidence.EvidenceHashVersion, staleEvidence.EvidenceHash, CancellationToken.None));
        Assert.Equal("EVIDENCE_STALE", stale.Code);

        db.SignatureRecords.Add(new SignatureRecord(
            qa.Id, "qa", "batch.release.esign", "ProductionBatch", created.Id.ToString(),
            ElectronicSignature.Batch("batch.release.esign"), "legacy"));
        await db.SaveChangesAsync();
        var releaseEvidence = await qaQuery.RecordAsync(created.Id, CancellationToken.None);
        Assert.Equal("Unbound", Assert.Single(releaseEvidence.Esigns!,
            e => e.Action == "batch.release.esign").Integrity);
        var released = await qaBatches.ReleaseAsync(created.Id, "对照样品放行", "Quality@123",
            releaseEvidence.EvidenceHashVersion, releaseEvidence.EvidenceHash, CancellationToken.None);
        Assert.Equal(BatchStatus.Released, released.Status);

        var genealogy = await opLots.GenealogyAsync(charge.Id, CancellationToken.None);
        Assert.Contains(genealogy.Descendants, d => d.LotNumber == "INGOT-IT-01-S1");
        Assert.Contains(genealogy.Uses, u => u.BatchNo == "BLOT1" && u.Role == MaterialUseRole.Charge);

        var record = await qaQuery.RecordAsync(created.Id, CancellationToken.None);
        Assert.Contains(record.Materials!, m => m.Role == MaterialUseRole.Produced && m.LotNumber == "PROD-IT-01");
        Assert.Contains(record.LabSamples!, s => s.SampleCode == "QC-IT-1" && s.Disposition == LabSampleDisposition.Pass);
        var produced = await db.MaterialLots.SingleAsync(l => l.LotNumber == "PROD-IT-01");
        Assert.Equal(MaterialLotStatus.Released, produced.Status);
        var charged = await db.MaterialLots.SingleAsync(l => l.LotNumber == "INGOT-IT-01-S1");
        Assert.Equal(MaterialLotStatus.Consumed, charged.Status);

        // —— 电子签名是结构化记录：含义原文在签署时冻结，备注与含义分开存 ——
        var release = Assert.Single(record.Esigns!,
            e => e.Action == "batch.release.esign" && e.Integrity == "Verified");
        Assert.Equal(ElectronicSignature.Batch("batch.release.esign"), release.Meaning);
        Assert.Equal("对照样品放行", release.Extra);
        Assert.Equal("qa", release.UserName);
        Assert.Equal(BatchRecordEvidenceHash.CurrentVersion, release.ContentHashVersion);
        Assert.Equal(releaseEvidence.EvidenceHash, release.ContentHash);
        Assert.Equal("Verified", release.Integrity);

        var labSignature = await db.SignatureRecords.SingleAsync(s => s.Action == "lab.sample.dispose.esign");
        Assert.Equal("LabSample", labSignature.EntityType);
        Assert.Equal(sample.Id.ToString(), labSignature.EntityId);
        Assert.Equal(ElectronicSignature.Batch("lab.sample.dispose.esign"), labSignature.Meaning);
        Assert.Equal(LabSampleSignatureContent.CurrentVersion, labSignature.ContentHashVersion);
        Assert.Matches("^[A-F0-9]{64}$", labSignature.ContentHash);
        var signedSample = Assert.Single(record.LabSamples!, s => s.Id == sample.Id);
        Assert.Equal("Verified", signedSample.DispositionSignature?.Integrity);
        Assert.Equal(labSignature.ContentHash, signedSample.DispositionSignature?.ContentHash);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE signature_records SET \"ContentHashVersion\" = NULL WHERE \"Id\" = {labSignature.Id}");
        var partialHash = await opLots.SamplesForBatchAsync(created.Id, CancellationToken.None);
        Assert.Equal("Mismatch", Assert.Single(partialHash, s => s.Id == sample.Id)
            .DispositionSignature?.Integrity);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE signature_records SET \"ContentHashVersion\" = {LabSampleSignatureContent.CurrentVersion} WHERE \"Id\" = {labSignature.Id}");

        // 审计履历不因此缺一行。
        Assert.Contains(db.AuditLogs, a => a.Action == "batch.release.esign" && a.EntityId == created.Id.ToString());

        // 批记录读的是库里冻结的文本，而不是拿当前代码里的含义表重算：
        // 塞一条"当时的措辞与现行代码不同"的签名，展示必须是当时那句。
        const string AtTheTime = "当时的措辞：与现行代码里的这一句不同。";
        db.SignatureRecords.Add(new SignatureRecord(
            qa.Id, "qa", "batch.confirm.esign", "ProductionBatch", created.Id.ToString(), AtTheTime, null));
        await db.SaveChangesAsync();
        var again = await qaQuery.RecordAsync(created.Id, CancellationToken.None);
        Assert.Contains(again.Esigns!, e => e.Action == "batch.confirm.esign" && e.Meaning == AtTheTime);

        var legacySample = new LabSample(
            "QC-LEGACY-1", created.Id, LabSampleType.Retain, "operator", DateTimeOffset.UtcNow,
            resultsJson: """{"hardness":40}""");
        legacySample.RecordDisposition(LabSampleDisposition.Pass, "质量工程师", "旧签名", DateTimeOffset.UtcNow);
        db.LabSamples.Add(legacySample);
        db.SignatureRecords.Add(new SignatureRecord(
            qa.Id, "质量工程师", "lab.sample.dispose.esign", "LabSample", legacySample.Id.ToString(),
            ElectronicSignature.Batch("lab.sample.dispose.esign"), "legacy"));
        await db.SaveChangesAsync();
        var historical = await opLots.SamplesForBatchAsync(created.Id, CancellationToken.None);
        Assert.Equal("Unbound", Assert.Single(historical, s => s.Id == legacySample.Id)
            .DispositionSignature?.Integrity);

        const string TamperedResults = """{"hardness":999}""";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE lab_samples SET \"ResultsJson\" = {TamperedResults} WHERE \"Id\" = {sample.Id}");
        var tampered = await opLots.SamplesForBatchAsync(created.Id, CancellationToken.None);
        Assert.Equal("Mismatch", Assert.Single(tampered, s => s.Id == sample.Id).DispositionSignature?.Integrity);
        var tamperedRecord = await qaQuery.RecordAsync(created.Id, CancellationToken.None);
        Assert.Equal("Mismatch", Assert.Single(tamperedRecord.Esigns!,
            e => e.Action == "batch.release.esign" && e.ContentHashVersion is not null).Integrity);

        var rejected = await batches.CreateAsync(
            new CreateBatchRequest("BLOT2", recipe.Id, equipment.Id, 1, "PROD-IT-02"),
            CancellationToken.None);
        var rejectedBatch = await db.Batches.SingleAsync(b => b.Id == rejected.Id);
        rejectedBatch.Complete(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        var rejectEvidence = await qaQuery.RecordAsync(rejected.Id, CancellationToken.None);
        var rejectedResult = await qaBatches.RejectDispositionAsync(
            rejected.Id, "拒收理由", "Quality@123",
            rejectEvidence.EvidenceHashVersion, rejectEvidence.EvidenceHash, CancellationToken.None);
        Assert.Equal(BatchStatus.DispositionRejected, rejectedResult.Status);
        var rejectedRecord = await qaQuery.RecordAsync(rejected.Id, CancellationToken.None);
        var reject = Assert.Single(rejectedRecord.Esigns!, e => e.Action == "batch.reject.esign");
        Assert.Equal(rejectEvidence.EvidenceHash, reject.ContentHash);
        Assert.Equal("Verified", reject.Integrity);
    }
}
