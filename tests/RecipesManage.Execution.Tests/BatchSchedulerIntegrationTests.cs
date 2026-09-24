using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Recipes;
using RecipesManage.Execution;
using RecipesManage.Infrastructure.Persistence;
using RecipesManage.Infrastructure.Plc;
using Xunit;
using static RecipesManage.Execution.Tests.SchedulerHarness;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 引擎闭环测试。建 host / 批准配方 / 封存快照 / 排队批次这些工装在 <see cref="SchedulerHarness"/>，
/// 与 <see cref="PhaseGateMatrixTests"/> 共用一份，不再逐条测试各抄一遍。
/// </summary>
public sealed class BatchSchedulerIntegrationTests
{
    [Fact]
    public async Task Scheduler_WritesPlcInTopologyOrder_WithFourStepHandshake()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-{Guid.NewGuid():N}.db");
        Guid batchId;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
                var equipment = new EquipmentLine(
                    "HT-T", "test furnace", PlcProtocol.Simulator, "127.0.0.1", 102,
                    "S7_1200", 0, 1, "{}", "it");
                db.Equipment.Add(equipment);

                var recipe = MasterRecipe.Create("IT-HT", "handshake it", "P", "part", null, Guid.NewGuid());
                var draft = recipe.RequireDraft();
                var s1 = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                    [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                     new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)]);
                var s2 = new RecipeStep(draft.Id, "S20", "hold", StepType.Hold, 1, 100, 0, 30, null,
                    [new RecipeParameter(0, "保温温度", "℃", 120, 100, 200, true, true),
                     new RecipeParameter(1, "保温时长", "s", 1, 0.5, 5, true, true)]);
                draft.ReplaceProcedure([s1, s2], [new RecipeEdge(draft.Id, s1.Id, s2.Id)]);
                draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
                draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                recipe.MarkApproved(draft);
                db.Recipes.Add(recipe);
                await db.SaveChangesAsync();

                var snapshot = ControlRecipeSnapshotFactory.From(recipe, draft, DateTimeOffset.UtcNow);
                SnapshotIntegrity.Seal(snapshot, BatchService.JsonOptions, out var json);
                var batch = ProductionBatch.Create("BIT1", equipment.Id, snapshot, json, Guid.NewGuid());
                foreach (var step in snapshot.Steps)
                    batch.StepExecutions.Add(new BatchStepExecution(batch.Id, step.StepId, step.Code, step.Name, step.Type, step.Ordinal));
                batch.Queue();
                db.Batches.Add(batch);
                await db.SaveChangesAsync();
                batchId = batch.Id;
            }

            await host.StartAsync();

            ProductionBatch? live = null;
            var deadline = DateTime.UtcNow.AddSeconds(25);
            do
            {
                await Task.Delay(250);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.NotNull(live);
            Assert.Equal(BatchStatus.Completed, live.Status);
            Assert.Equal(2, live.StepExecutions.Count);
            Assert.All(live.StepExecutions, e => Assert.Equal(StepOutcome.Completed, e.Outcome));

            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Contains(log, e => e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_ID=10"));
                Assert.Contains(log, e => e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_ID=20"));
                Assert.Contains(log, e => e.Kind == "isa88" && e.Detail != null && e.Detail.Contains("Unit Procedure"));
                Assert.Contains(log, e => e.Kind == "trigger" && e.Detail != null && e.Detail.Contains("Trigger_Write=1"));
                Assert.Contains(log, e => e.Kind == "archive");
                Assert.Contains(log, e => e.Kind == "advance");
                Assert.Contains(log, e => e.StepCode == "S10");
                Assert.Contains(log, e => e.StepCode == "S20");
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_SkipAtPlcReady_ThenCompletesNextStep()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-skip-{Guid.NewGuid():N}.db");
        Guid batchId;
        Guid equipmentId;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
                var equipment = new EquipmentLine(
                    "HT-S", "skip furnace", PlcProtocol.Simulator, "127.0.0.1", 102,
                    "S7_1200", 0, 1, "{}", "it");
                db.Equipment.Add(equipment);
                var recipe = MasterRecipe.Create("IT-SK", "skip it", "P", "part", null, Guid.NewGuid());
                var draft = recipe.RequireDraft();
                var s1 = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                    [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                     new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)]);
                var s2 = new RecipeStep(draft.Id, "S20", "hold", StepType.Hold, 1, 100, 0, 30, null,
                    [new RecipeParameter(0, "保温温度", "℃", 120, 100, 200, true, true),
                     new RecipeParameter(1, "保温时长", "s", 1, 0.5, 5, true, true)]);
                draft.ReplaceProcedure([s1, s2], [new RecipeEdge(draft.Id, s1.Id, s2.Id)]);
                draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
                draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                recipe.MarkApproved(draft);
                db.Recipes.Add(recipe);
                await db.SaveChangesAsync();

                var snapshot = ControlRecipeSnapshotFactory.From(recipe, draft, DateTimeOffset.UtcNow);
                SnapshotIntegrity.Seal(snapshot, BatchService.JsonOptions, out var json);
                var batch = ProductionBatch.Create("BITSKIP", equipment.Id, snapshot, json, Guid.NewGuid());
                foreach (var step in snapshot.Steps)
                    batch.StepExecutions.Add(new BatchStepExecution(batch.Id, step.StepId, step.Code, step.Name, step.Type, step.Ordinal));
                batch.Queue();
                db.Batches.Add(batch);
                await db.SaveChangesAsync();
                batchId = batch.Id;
                equipmentId = equipment.Id;
            }

            host.Services.GetRequiredService<SimulatedPlcRack>().Get(equipmentId).InjectFault("HoldNotReady");
            await host.StartAsync();

            var scheduler = host.Services.GetRequiredService<IBatchScheduler>();
            ProductionBatch? live = null;
            var waitReady = DateTime.UtcNow.AddSeconds(8);
            do
            {
                await Task.Delay(200);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: not BatchStatus.Running } && DateTime.UtcNow < waitReady);

            Assert.Equal(BatchStatus.Running, live?.Status);
            await scheduler.EnqueueSkipAsync(batchId, "集成测试跳步");
            host.Services.GetRequiredService<SimulatedPlcRack>().Get(equipmentId).InjectFault("None");

            var deadline = DateTime.UtcNow.AddSeconds(20);
            do
            {
                await Task.Delay(250);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.NotNull(live);
            Assert.Equal(BatchStatus.Completed, live.Status);
            Assert.Equal(StepOutcome.Skipped, live.StepExecutions.Single(s => s.StepCode == "S10").Outcome);
            Assert.Equal(StepOutcome.Completed, live.StepExecutions.Single(s => s.StepCode == "S20").Outcome);

            using (var alarmScope = host.Services.CreateScope())
            {
                var db = alarmScope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Empty(await db.ProcessAlarms.AsNoTracking().Where(a => a.BatchId == batchId).ToListAsync());
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_HoldAtPlcReady_ThenResume_CompletesBatch()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-hold-{Guid.NewGuid():N}.db");
        Guid batchId;
        Guid equipmentId;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, equipmentId) = await SeedTwoStepBatchAsync(db, "HT-H", "IT-HD", "BITHOLD");
            }

            host.Services.GetRequiredService<SimulatedPlcRack>().Get(equipmentId).InjectFault("HoldNotReady");
            await host.StartAsync();

            var scheduler = host.Services.GetRequiredService<IBatchScheduler>();
            ProductionBatch? live = null;
            var waitReady = DateTime.UtcNow.AddSeconds(8);
            do
            {
                await Task.Delay(200);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: not BatchStatus.Running } && DateTime.UtcNow < waitReady);

            Assert.Equal(BatchStatus.Running, live?.Status);
            await scheduler.EnqueueHoldAsync(batchId, "集成测试保持");

            var waitHeld = DateTime.UtcNow.AddSeconds(8);
            do
            {
                await Task.Delay(200);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: not BatchStatus.Held } && DateTime.UtcNow < waitHeld);

            Assert.Equal(BatchStatus.Held, live?.Status);
            Assert.Contains(live!.StepExecutions, s => s.Outcome == StepOutcome.Pending);

            host.Services.GetRequiredService<SimulatedPlcRack>().Get(equipmentId).InjectFault("None");
            using (var resumeScope = host.Services.CreateScope())
            {
                var db = resumeScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var tracked = await db.Batches.SingleAsync(b => b.Id == batchId);
                tracked.Resume();
                await db.SaveChangesAsync();
            }
            await scheduler.EnqueueStartAsync(batchId);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            do
            {
                await Task.Delay(250);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.NotNull(live);
            Assert.Equal(BatchStatus.Completed, live.Status);
            Assert.All(live.StepExecutions, e => Assert.Equal(StepOutcome.Completed, e.Outcome));

            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Contains(log, e => e.Kind == "hold");
                Assert.Contains(log, e => e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_ID=10"));
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_HoldDuringStepRunning_WritesHostHold_ThenResumeCompletes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-holdrun-{Guid.NewGuid():N}.db");
        Guid batchId;
        Guid equipmentId;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                var equipment = new EquipmentLine(
                    "HT-HR", "run hold", PlcProtocol.Simulator, "127.0.0.1", 102,
                    "S7_1200", 0, 1, "{}", "it");
                db.Equipment.Add(equipment);
                var recipe = MasterRecipe.Create("ITHR", "run hold", "P", "part", null, Guid.NewGuid());
                var draft = recipe.RequireDraft();
                var s1 = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                    [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                     new RecipeParameter(1, "时长", "s", 8, 1, 20, true, true)]);
                var s2 = new RecipeStep(draft.Id, "S20", "hold", StepType.Hold, 1, 100, 0, 30, null,
                    [new RecipeParameter(0, "保温温度", "℃", 120, 100, 200, true, true),
                     new RecipeParameter(1, "保温时长", "s", 1, 0.5, 5, true, true)]);
                draft.ReplaceProcedure([s1, s2], [new RecipeEdge(draft.Id, s1.Id, s2.Id)]);
                draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
                draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                recipe.MarkApproved(draft);
                db.Recipes.Add(recipe);
                await db.SaveChangesAsync();
                var snapshot = ControlRecipeSnapshotFactory.From(recipe, draft, DateTimeOffset.UtcNow);
                SnapshotIntegrity.Seal(snapshot, BatchService.JsonOptions, out var json);
                var batch = ProductionBatch.Create("BITHR", equipment.Id, snapshot, json, Guid.NewGuid());
                foreach (var step in snapshot.Steps)
                    batch.StepExecutions.Add(new BatchStepExecution(batch.Id, step.StepId, step.Code, step.Name, step.Type, step.Ordinal));
                batch.Queue();
                db.Batches.Add(batch);
                await db.SaveChangesAsync();
                batchId = batch.Id;
                equipmentId = equipment.Id;
            }

            await host.StartAsync();
            var scheduler = host.Services.GetRequiredService<IBatchScheduler>();

            ProductionBatch? live = null;
            var waitRun = DateTime.UtcNow.AddSeconds(8);
            do
            {
                await Task.Delay(120);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { HandshakePhase: not "StepRunning" } && DateTime.UtcNow < waitRun);

            Assert.Equal("StepRunning", live?.HandshakePhase);
            Assert.Equal(BatchStatus.Running, live?.Status);

            var holdStarted = DateTime.UtcNow;
            await scheduler.EnqueueHoldAsync(batchId, "运行中保持");
            var waitHeld = DateTime.UtcNow.AddSeconds(4);
            do
            {
                await Task.Delay(100);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: not BatchStatus.Held } && DateTime.UtcNow < waitHeld);

            Assert.Equal(BatchStatus.Held, live?.Status);
            Assert.True(DateTime.UtcNow - holdStarted < TimeSpan.FromSeconds(3), "运行中保持必须在 PLC_Held 应答后立即生效，不能等工步跑完。");
            Assert.Equal(StepOutcome.Held, live!.StepExecutions.Single(s => s.StepCode == "S10").Outcome);
            Assert.Equal(StepOutcome.Pending, live.StepExecutions.Single(s => s.StepCode == "S20").Outcome);
            Assert.True(host.Services.GetRequiredService<SimulatedPlcRack>().Get(equipmentId).ReadSignals().PlcHeld);

            using (var resumeScope = host.Services.CreateScope())
            {
                var db = resumeScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var tracked = await db.Batches.SingleAsync(b => b.Id == batchId);
                tracked.Resume();
                await db.SaveChangesAsync();
            }
            await scheduler.EnqueueStartAsync(batchId);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            do
            {
                await Task.Delay(250);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.Equal(BatchStatus.Completed, live?.Status);
            Assert.All(live!.StepExecutions, e => Assert.Equal(StepOutcome.Completed, e.Outcome));

            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Contains(log, e => e.Kind == "hold" && e.Detail != null && e.Detail.Contains("Host_Hold"));
                Assert.Contains(log, e => e.Kind == "resume" && e.Detail != null && e.Detail.Contains("Host_Hold=0"));
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_WriteEchoMismatch_FaultsBeforeTriggerWrite()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-echo-{Guid.NewGuid():N}.db");
        Guid batchId;
        Guid equipmentId;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, equipmentId) = await SeedTwoStepBatchAsync(db, "HT-EC", "ITEC", "BITECHO");
            }

            host.Services.GetRequiredService<SimulatedPlcRack>().Get(equipmentId).InjectFault("CorruptEcho");
            await host.StartAsync();

            ProductionBatch? live = null;
            var deadline = DateTime.UtcNow.AddSeconds(12);
            do
            {
                await Task.Delay(150);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: not BatchStatus.Faulted } && DateTime.UtcNow < deadline);

            Assert.Equal(BatchStatus.Faulted, live?.Status);
            Assert.Equal(nameof(HandshakeFaultCode.WriteVerifyMismatch), live?.FaultCode);

            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Contains(log, e => e.Kind == "verify" && e.Detail != null && e.Detail.Contains("不一致"));
                Assert.DoesNotContain(log, e => e.Kind == "trigger" && e.Detail != null && e.Detail.Contains("Trigger_Write=1"));
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_ParallelUnits_OnTwoSimulators_JoinThenComplete()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-par-{Guid.NewGuid():N}.db");
        Guid batchId;
        Guid eqA;
        Guid eqB;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                var a = new EquipmentLine("HT-PA", "unit A", PlcProtocol.Simulator, "127.0.0.1", 102,
                    "S7_1200", 0, 1, "{}", "it");
                var b = new EquipmentLine("HT-PB", "unit B", PlcProtocol.Simulator, "127.0.0.1", 102,
                    "S7_1200", 0, 1, "{}", "it");
                db.Equipment.AddRange(a, b);

                var recipe = MasterRecipe.Create("IT-PAR", "parallel units", "P", "part", null, Guid.NewGuid());
                var draft = recipe.RequireDraft();
                var s10 = new RecipeStep(draft.Id, "S10", "heat A", StepType.Heat, 0, 0, 0, 30, null,
                    [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                     new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)],
                    unitProcedure: "UP-A");
                var s20 = new RecipeStep(draft.Id, "S20", "cool B", StepType.Cool, 1, 100, 0, 30, null,
                    [new RecipeParameter(0, "终点温度", "℃", 40, 20, 60, true, true),
                     new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)],
                    unitProcedure: "UP-B");
                var s30 = new RecipeStep(draft.Id, "S30", "qc join", StepType.QualityCheck, 2, 50, 80, 30, null,
                    [new RecipeParameter(0, "硬度", "HB", 95, 90, 110, false, true)],
                    unitProcedure: "UP-QC");
                draft.ReplaceProcedure(
                    [s10, s20, s30],
                    [new RecipeEdge(draft.Id, s10.Id, s30.Id), new RecipeEdge(draft.Id, s20.Id, s30.Id)]);
                draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
                draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                recipe.MarkApproved(draft);
                db.Recipes.Add(recipe);
                await db.SaveChangesAsync();

                var snapshot = ControlRecipeSnapshotFactory.From(
                    recipe, draft, DateTimeOffset.UtcNow, 1, null,
                    new Dictionary<string, Guid> { ["UP-A"] = a.Id, ["UP-B"] = b.Id, ["UP-QC"] = a.Id },
                    a.Id);
                Assert.NotNull(snapshot.UnitEquipment);
                SnapshotIntegrity.Seal(snapshot, BatchService.JsonOptions, out var json);
                var batch = ProductionBatch.Create("BITPAR", a.Id, snapshot, json, Guid.NewGuid());
                foreach (var step in snapshot.Steps)
                    batch.StepExecutions.Add(new BatchStepExecution(batch.Id, step.StepId, step.Code, step.Name, step.Type, step.Ordinal));
                batch.Queue();
                db.Batches.Add(batch);
                await db.SaveChangesAsync();
                batchId = batch.Id;
                eqA = a.Id;
                eqB = b.Id;
            }

            host.Services.GetRequiredService<SimulatedPlcRack>().Get(eqA).InjectFault("HoldNotReady");
            await host.StartAsync();

            ProductionBatch? live = null;
            var waitPeer = DateTime.UtcNow.AddSeconds(12);
            do
            {
                await Task.Delay(200);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live?.StepExecutions.Single(s => s.StepCode == "S20").Outcome != StepOutcome.Completed && DateTime.UtcNow < waitPeer);

            Assert.Equal(BatchStatus.Running, live?.Status);
            Assert.Equal(StepOutcome.Completed, live!.StepExecutions.Single(s => s.StepCode == "S20").Outcome);
            Assert.NotEqual(StepOutcome.Completed, live.StepExecutions.Single(s => s.StepCode == "S10").Outcome);
            Assert.NotEqual(StepOutcome.Completed, live.StepExecutions.Single(s => s.StepCode == "S30").Outcome);
            Assert.Contains("HT-PA", live.HandshakePhase);
            Assert.Contains("HT-PB", live.HandshakePhase);

            host.Services.GetRequiredService<SimulatedPlcRack>().Get(eqA).InjectFault("None");

            var deadline = DateTime.UtcNow.AddSeconds(20);
            do
            {
                await Task.Delay(250);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.NotNull(live);
            Assert.Equal(BatchStatus.Completed, live.Status);
            Assert.All(live.StepExecutions, e => Assert.Equal(StepOutcome.Completed, e.Outcome));

            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Contains(log, e => e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_ID=10"));
                Assert.Contains(log, e => e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_ID=20"));
                Assert.DoesNotContain(log, e => e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_ID=30"));
                Assert.Contains(log, e => e.StepCode == "S30" && e.Kind == "quality" && e.Detail != null && e.Detail.Contains("禁止写 PLC"));
                Assert.Contains(log, e => e.Kind == "isa88" && e.Detail != null && e.Detail.Contains("UP-A"));
                Assert.Contains(log, e => e.Kind == "isa88" && e.Detail != null && e.Detail.Contains("UP-B"));
                Assert.Contains(log, e => e.Kind == "advance");
            }

            Assert.Contains(events, e => e.Type == "handshake" && EquipmentCodeOf(e) == "HT-PA");
            Assert.Contains(events, e => e.Type == "handshake" && EquipmentCodeOf(e) == "HT-PB");
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_ModbusTcpLoopback_CompletesFourStepHandshake()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-mb-{Guid.NewGuid():N}.db");
        Guid batchId;
        var events = new ConcurrentBag<ExecutionEvent>();
        await using var slave = new ModbusTcpHandshakeSlave();
        await slave.StartAsync(0);
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                var map = System.Text.Json.JsonSerializer.Serialize(HandshakeTagMap.ModbusLoopback());
                var equipment = new EquipmentLine(
                    "MB-IT", "modbus loop", PlcProtocol.ModbusTcp, "127.0.0.1", slave.Port,
                    "MODBUS", 0, 1, map, "it");
                db.Equipment.Add(equipment);
                var recipe = MasterRecipe.Create("IT-MB", "modbus it", "P", "part", null, Guid.NewGuid());
                var draft = recipe.RequireDraft();
                var s1 = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                    [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                     new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)]);
                draft.ReplaceProcedure([s1], []);
                draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
                draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                recipe.MarkApproved(draft);
                db.Recipes.Add(recipe);
                await db.SaveChangesAsync();

                var snapshot = ControlRecipeSnapshotFactory.From(recipe, draft, DateTimeOffset.UtcNow);
                SnapshotIntegrity.Seal(snapshot, BatchService.JsonOptions, out var json);
                var batch = ProductionBatch.Create("BITMB", equipment.Id, snapshot, json, Guid.NewGuid());
                foreach (var step in snapshot.Steps)
                    batch.StepExecutions.Add(new BatchStepExecution(batch.Id, step.StepId, step.Code, step.Name, step.Type, step.Ordinal));
                batch.Queue();
                db.Batches.Add(batch);
                await db.SaveChangesAsync();
                batchId = batch.Id;
            }

            await host.StartAsync();
            ProductionBatch? live = null;
            var deadline = DateTime.UtcNow.AddSeconds(25);
            do
            {
                await Task.Delay(250);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.NotNull(live);
            Assert.Equal(BatchStatus.Completed, live.Status);
            Assert.All(live.StepExecutions, e => Assert.Equal(StepOutcome.Completed, e.Outcome));
            using var logScope = host.Services.CreateScope();
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Contains(log, e => e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_ID=10"));
                Assert.Contains(log, e => e.Kind == "trigger");
                Assert.Contains(log, e => e.Kind == "archive");
                Assert.Contains(log, e => e.Kind == "advance");
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_OpcUaLoopback_CompletesFourStepHandshake()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-ua-{Guid.NewGuid():N}.db");
        Guid batchId;
        var events = new ConcurrentBag<ExecutionEvent>();
        await using var slave = new OpcUaHandshakeSlave();
        await slave.StartAsync(0);
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                var map = System.Text.Json.JsonSerializer.Serialize(HandshakeTagMap.OpcUaLoopback());
                var equipment = new EquipmentLine(
                    "UA-IT", "opcua loop", PlcProtocol.OpcUa, slave.Endpoint, slave.Port,
                    "OPC_UA", 0, 1, map, "it");
                db.Equipment.Add(equipment);
                var recipe = MasterRecipe.Create("IT-UA", "opcua it", "P", "part", null, Guid.NewGuid());
                var draft = recipe.RequireDraft();
                var s1 = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                    [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                     new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)]);
                draft.ReplaceProcedure([s1], []);
                draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
                draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                recipe.MarkApproved(draft);
                db.Recipes.Add(recipe);
                await db.SaveChangesAsync();

                var snapshot = ControlRecipeSnapshotFactory.From(recipe, draft, DateTimeOffset.UtcNow);
                SnapshotIntegrity.Seal(snapshot, BatchService.JsonOptions, out var json);
                var batch = ProductionBatch.Create("BITUA", equipment.Id, snapshot, json, Guid.NewGuid());
                foreach (var step in snapshot.Steps)
                    batch.StepExecutions.Add(new BatchStepExecution(batch.Id, step.StepId, step.Code, step.Name, step.Type, step.Ordinal));
                batch.Queue();
                db.Batches.Add(batch);
                await db.SaveChangesAsync();
                batchId = batch.Id;
            }

            await host.StartAsync();
            ProductionBatch? live = null;
            var deadline = DateTime.UtcNow.AddSeconds(40);
            do
            {
                await Task.Delay(250);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.NotNull(live);
            Assert.Equal(BatchStatus.Completed, live.Status);
            Assert.All(live.StepExecutions, e => Assert.Equal(StepOutcome.Completed, e.Outcome));
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_SiemensS7Loopback_CompletesFourStepHandshake()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-s7-{Guid.NewGuid():N}.db");
        Guid batchId;
        var events = new ConcurrentBag<ExecutionEvent>();
        await using var slave = new SiemensS7HandshakeSlave();
        await slave.StartAsync(0);
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                var map = System.Text.Json.JsonSerializer.Serialize(new HandshakeTagMap());
                var equipment = new EquipmentLine(
                    "S7-IT", "s7 loop", PlcProtocol.SiemensS7, "127.0.0.1", slave.Port,
                    "S7_1200", 0, 1, map, "it");
                db.Equipment.Add(equipment);
                var recipe = MasterRecipe.Create("IT-S7", "s7 it", "P", "part", null, Guid.NewGuid());
                var draft = recipe.RequireDraft();
                var s1 = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                    [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                     new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)]);
                draft.ReplaceProcedure([s1], []);
                draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
                draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                recipe.MarkApproved(draft);
                db.Recipes.Add(recipe);
                await db.SaveChangesAsync();

                var snapshot = ControlRecipeSnapshotFactory.From(recipe, draft, DateTimeOffset.UtcNow);
                SnapshotIntegrity.Seal(snapshot, BatchService.JsonOptions, out var json);
                var batch = ProductionBatch.Create("BITS7", equipment.Id, snapshot, json, Guid.NewGuid());
                foreach (var step in snapshot.Steps)
                    batch.StepExecutions.Add(new BatchStepExecution(batch.Id, step.StepId, step.Code, step.Name, step.Type, step.Ordinal));
                batch.Queue();
                db.Batches.Add(batch);
                await db.SaveChangesAsync();
                batchId = batch.Id;
            }

            await host.StartAsync();
            ProductionBatch? live = null;
            var deadline = DateTime.UtcNow.AddSeconds(25);
            do
            {
                await Task.Delay(250);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.NotNull(live);
            Assert.Equal(BatchStatus.Completed, live.Status);
            Assert.All(live.StepExecutions, e => Assert.Equal(StepOutcome.Completed, e.Outcome));
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_SkipOneParallelLane_DoesNotSkipPeer()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-par-skip-{Guid.NewGuid():N}.db");
        Guid batchId;
        Guid eqA;
        Guid eqB;
        Guid stepA;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                var a = new EquipmentLine("HT-SA", "unit A", PlcProtocol.Simulator, "127.0.0.1", 102,
                    "S7_1200", 0, 1, "{}", "it");
                var b = new EquipmentLine("HT-SB", "unit B", PlcProtocol.Simulator, "127.0.0.1", 102,
                    "S7_1200", 0, 1, "{}", "it");
                db.Equipment.AddRange(a, b);

                var recipe = MasterRecipe.Create("IT-PSK", "parallel skip", "P", "part", null, Guid.NewGuid());
                var draft = recipe.RequireDraft();
                var s10 = new RecipeStep(draft.Id, "S10", "heat A", StepType.Heat, 0, 0, 0, 30, null,
                    [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                     new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)],
                    unitProcedure: "UP-A");
                var s20 = new RecipeStep(draft.Id, "S20", "cool B", StepType.Cool, 1, 100, 0, 30, null,
                    [new RecipeParameter(0, "终点温度", "℃", 40, 20, 60, true, true),
                     new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)],
                    unitProcedure: "UP-B");
                var s30 = new RecipeStep(draft.Id, "S30", "qc join", StepType.QualityCheck, 2, 50, 80, 30, null,
                    [new RecipeParameter(0, "硬度", "HB", 95, 90, 110, false, true)],
                    unitProcedure: "UP-QC");
                draft.ReplaceProcedure(
                    [s10, s20, s30],
                    [new RecipeEdge(draft.Id, s10.Id, s30.Id), new RecipeEdge(draft.Id, s20.Id, s30.Id)]);
                draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
                draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                recipe.MarkApproved(draft);
                db.Recipes.Add(recipe);
                await db.SaveChangesAsync();

                var snapshot = ControlRecipeSnapshotFactory.From(
                    recipe, draft, DateTimeOffset.UtcNow, 1, null,
                    new Dictionary<string, Guid> { ["UP-A"] = a.Id, ["UP-B"] = b.Id, ["UP-QC"] = a.Id },
                    a.Id);
                SnapshotIntegrity.Seal(snapshot, BatchService.JsonOptions, out var json);
                var batch = ProductionBatch.Create("BITPSK", a.Id, snapshot, json, Guid.NewGuid());
                foreach (var step in snapshot.Steps)
                    batch.StepExecutions.Add(new BatchStepExecution(batch.Id, step.StepId, step.Code, step.Name, step.Type, step.Ordinal));
                batch.Queue();
                db.Batches.Add(batch);
                await db.SaveChangesAsync();
                batchId = batch.Id;
                eqA = a.Id;
                eqB = b.Id;
                stepA = snapshot.Steps.Single(s => s.Code == "S10").StepId;
            }

            var rack = host.Services.GetRequiredService<SimulatedPlcRack>();
            rack.Get(eqA).InjectFault("HoldNotReady");
            rack.Get(eqB).InjectFault("HoldNotReady");
            await host.StartAsync();

            ProductionBatch? live = null;
            var waitReady = DateTime.UtcNow.AddSeconds(8);
            do
            {
                await Task.Delay(200);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: not BatchStatus.Running } && DateTime.UtcNow < waitReady);

            Assert.Equal(BatchStatus.Running, live?.Status);
            await host.Services.GetRequiredService<IBatchScheduler>()
                .EnqueueSkipAsync(batchId, "只跳 UP-A", stepA);
            rack.Get(eqA).InjectFault("None");
            rack.Get(eqB).InjectFault("None");

            var deadline = DateTime.UtcNow.AddSeconds(20);
            do
            {
                await Task.Delay(250);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.NotNull(live);
            Assert.Equal(BatchStatus.Completed, live.Status);
            Assert.Equal(StepOutcome.Skipped, live.StepExecutions.Single(s => s.StepCode == "S10").Outcome);
            Assert.Equal(StepOutcome.Completed, live.StepExecutions.Single(s => s.StepCode == "S20").Outcome);
            Assert.Equal(StepOutcome.Completed, live.StepExecutions.Single(s => s.StepCode == "S30").Outcome);
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_ManualConfirm_DoesNotWritePlc_UntilEsign()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-confirm-{Guid.NewGuid():N}.db");
        Guid batchId;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, _) = await SeedApprovedBatchAsync(db, "HT-CF", "ITCF", "BITCFM", draftId =>
                [
                    new RecipeStep(draftId, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                        [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                         new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)]),
                    new RecipeStep(draftId, "S20", "confirm", StepType.ManualConfirm, 1, 100, 0, 30, null, [])
                ]);
            }

            await host.StartAsync();
            var scheduler = host.Services.GetRequiredService<IBatchScheduler>();
            ProductionBatch? live = null;
            var waitConfirm = DateTime.UtcNow.AddSeconds(20);
            do
            {
                await Task.Delay(200);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live!.StepExecutions.Single(s => s.StepCode == "S20").Outcome != StepOutcome.AwaitingConfirm
                     && DateTime.UtcNow < waitConfirm);

            Assert.Equal(BatchStatus.Running, live.Status);
            Assert.Equal(StepOutcome.Completed, live.StepExecutions.Single(s => s.StepCode == "S10").Outcome);
            Assert.Equal(StepOutcome.AwaitingConfirm, live.StepExecutions.Single(s => s.StepCode == "S20").Outcome);

            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Contains(log, e => e.StepCode == "S10" && e.Kind == "trigger" && e.Detail != null && e.Detail.Contains("Trigger_Write=1"));
                Assert.DoesNotContain(log, e => e.StepCode == "S20" && e.Kind == "write");
                Assert.DoesNotContain(log, e => e.StepCode == "S20" && e.Kind == "trigger");
                Assert.Contains(log, e => e.StepCode == "S20" && e.Kind == "confirm" && e.Detail != null && e.Detail.Contains("禁止写 PLC"));
            }

            await scheduler.EnqueueConfirmAsync(batchId, "集成测试确认");

            var deadline = DateTime.UtcNow.AddSeconds(12);
            do
            {
                await Task.Delay(200);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.Equal(BatchStatus.Completed, live?.Status);
            Assert.All(live!.StepExecutions, e => Assert.Equal(StepOutcome.Completed, e.Outcome));

            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Contains(log, e => e.StepCode == "S20" && e.Kind == "confirm" && e.Detail != null && e.Detail.Contains("未写 PLC"));
                Assert.DoesNotContain(log, e => e.StepCode == "S20" && e.Kind == "write");
                Assert.DoesNotContain(log, e => e.StepCode == "S20" && e.Kind == "trigger");
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_QualityOos_HoldsBeforeNextStepWrite()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-oos-{Guid.NewGuid():N}.db");
        Guid batchId;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, _) = await SeedApprovedBatchAsync(db, "HT-OOS", "ITOOS", "BITOOS", draftId =>
                [
                    new RecipeStep(draftId, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                        [new RecipeParameter(0, "目标温度", "℃", 120, 200, 300, true, true),
                         new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)]),
                    new RecipeStep(draftId, "S20", "hold", StepType.Hold, 1, 100, 0, 30, null,
                        [new RecipeParameter(0, "保温温度", "℃", 120, 100, 200, true, true),
                         new RecipeParameter(1, "保温时长", "s", 1, 0.5, 5, true, true)])
                ]);
            }

            await host.StartAsync();

            ProductionBatch? live = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            do
            {
                await Task.Delay(200);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: not BatchStatus.Held } && DateTime.UtcNow < deadline);

            Assert.Equal(BatchStatus.Held, live?.Status);
            Assert.Contains("质检超差", live!.FaultMessage);
            Assert.Equal(StepOutcome.Completed, live.StepExecutions.Single(s => s.StepCode == "S10").Outcome);
            Assert.Equal(StepOutcome.Pending, live.StepExecutions.Single(s => s.StepCode == "S20").Outcome);

            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var alarms = await db.ProcessAlarms.AsNoTracking().Where(a => a.BatchId == batchId).ToListAsync();
                Assert.Contains(alarms, a => a.Code == "QualityOos" && a.Severity == "Quality");
                var log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Contains(log, e => e.Kind == "quality" && e.Detail != null && e.Detail.Contains("超差"));
                Assert.DoesNotContain(log, e => e.StepCode == "S20" && (e.Kind == "write" || e.Kind == "trigger"));
            }

            Assert.Contains(events, e => e.Type == "alarm");
            Assert.Contains(events, e => e.Type == "held");
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_QualityCheckAndWait_DoNotWritePlc()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-qcwait-{Guid.NewGuid():N}.db");
        Guid batchId;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, _) = await SeedApprovedBatchAsync(db, "HT-QW", "ITQW", "BITQW", draftId =>
                [
                    new RecipeStep(draftId, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                        [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                         new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)]),
                    new RecipeStep(draftId, "S20", "wait", StepType.Wait, 1, 100, 0, 30, null,
                        [new RecipeParameter(0, "等待时长", "s", 1, 0.5, 5, false, false)]),
                    new RecipeStep(draftId, "S30", "qc", StepType.QualityCheck, 2, 200, 0, 30, null,
                        [new RecipeParameter(0, "硬度", "HB", 95, 90, 110, false, true)])
                ]);
            }

            await host.StartAsync();
            ProductionBatch? live = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            do
            {
                await Task.Delay(200);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.Equal(BatchStatus.Completed, live?.Status);
            Assert.All(live!.StepExecutions, e => Assert.Equal(StepOutcome.Completed, e.Outcome));
            var qcJson = live.StepExecutions.Single(s => s.StepCode == "S30").QualityJson;
            Assert.DoesNotContain("硬度", qcJson);
            Assert.Contains("PLC:", qcJson, StringComparison.Ordinal);
            Assert.True(QualityDisposition.HasOutOfSpec(BatchService.Deserialize(live.ControlRecipeJson)!, live.StepExecutions));

            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Contains(log, e => e.StepCode == "S10" && e.Kind == "trigger" && e.Detail != null && e.Detail.Contains("Trigger_Write=1"));
                Assert.DoesNotContain(log, e => e.StepCode == "S20" && e.Kind == "write");
                Assert.DoesNotContain(log, e => e.StepCode == "S30" && e.Kind == "write");
                Assert.Contains(log, e => e.StepCode == "S20" && e.Kind == "wait" && e.Detail != null && e.Detail.Contains("禁止写 PLC"));
                Assert.Contains(log, e => e.StepCode == "S30" && e.Kind == "quality" && e.Detail != null && e.Detail.Contains("禁止写 PLC"));
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_MixPressureTransfer_WritesPlcInTopology_QualityDoesNotWrite()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-mpt-{Guid.NewGuid():N}.db");
        Guid batchId;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, _) = await SeedApprovedBatchAsync(db, "HT-MPT", "ITMPT", "BITMPT", draftId =>
                [
                    new RecipeStep(draftId, "S10", "mix", StepType.Mix, 0, 0, 0, 30, null,
                        [new RecipeParameter(0, "搅拌转速", "rpm", 60, 10, 200, true, false),
                         new RecipeParameter(1, "搅拌时长", "s", 1, 0.5, 5, true, true)],
                        null, "UP-混合", Isa88.DefaultOperation(StepType.Mix)),
                    new RecipeStep(draftId, "S20", "press", StepType.Pressure, 1, 100, 0, 30, null,
                        [new RecipeParameter(0, "目标压力", "bar", 2.5, 1, 6, true, true),
                         new RecipeParameter(1, "保压时长", "s", 1, 0.5, 5, true, false)],
                        null, "UP-加压", Isa88.DefaultOperation(StepType.Pressure)),
                    new RecipeStep(draftId, "S30", "xfer", StepType.Transfer, 2, 200, 0, 30, null,
                        [new RecipeParameter(0, "转移量", "kg", 50, 1, 500, true, true, true),
                         new RecipeParameter(1, "转移时长", "s", 1, 0.5, 5, true, false)],
                        null, "UP-转移", Isa88.DefaultOperation(StepType.Transfer)),
                    new RecipeStep(draftId, "S40", "qc", StepType.QualityCheck, 3, 300, 0, 30, null,
                        [new RecipeParameter(0, "硬度", "HB", 95, 90, 110, false, true)],
                        null, "UP-QC", Isa88.DefaultOperation(StepType.QualityCheck))
                ]);
            }

            await host.StartAsync();
            ProductionBatch? live = null;
            var deadline = DateTime.UtcNow.AddSeconds(25);
            do
            {
                await Task.Delay(200);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.Equal(BatchStatus.Completed, live?.Status);
            Assert.All(live!.StepExecutions, e => Assert.Equal(StepOutcome.Completed, e.Outcome));
            var qcJson = live.StepExecutions.Single(s => s.StepCode == "S40").QualityJson;
            Assert.DoesNotContain("硬度", qcJson);
            Assert.Contains("PLC:", qcJson, StringComparison.Ordinal);
            Assert.True(QualityDisposition.HasOutOfSpec(BatchService.Deserialize(live.ControlRecipeJson)!, live.StepExecutions));

            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Contains(log, e => e.StepCode == "S10" && e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_ID=10") && e.Detail.Contains("Step_Type=4"));
                Assert.Contains(log, e => e.StepCode == "S10" && e.Kind == "trigger" && e.Detail != null && e.Detail.Contains("Trigger_Write=1"));
                Assert.Contains(log, e => e.StepCode == "S20" && e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_ID=20") && e.Detail.Contains("Step_Type=5"));
                Assert.Contains(log, e => e.StepCode == "S30" && e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_ID=30") && e.Detail.Contains("Step_Type=6"));
                Assert.Contains(log, e => e.StepCode == "S40" && e.Kind == "quality" && e.Detail != null && e.Detail.Contains("禁止写 PLC"));
                Assert.DoesNotContain(log, e => e.StepCode == "S40" && e.Kind == "write");
                var writes = log.Where(e => e.Kind == "write").Select(e => e.StepCode).ToList();
                Assert.Equal(["S10", "S20", "S30"], writes);
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_CustomPlcProgramId_WritesStepType21And22()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-pgm-{Guid.NewGuid():N}.db");
        Guid batchId;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, _) = await SeedApprovedBatchAsync(db, "PR-PGM", "ITPGM", "BITPGM", draftId =>
                [
                    new RecipeStep(draftId, "S10", "水冲洗", StepType.Transfer, 0, 0, 0, 30, null,
                        [new RecipeParameter(0, "冲洗流量", "L/min", 12, 1, 40, true, false),
                         new RecipeParameter(1, "冲洗时长", "s", 1, 0.5, 5, true, false)],
                        null, "UP-冲洗", "OP-Rinse 水冲洗", 21),
                    new RecipeStep(draftId, "S20", "气缸保压", StepType.Pressure, 1, 100, 0, 30, null,
                        [new RecipeParameter(0, "气缸压力", "bar", 4, 1, 10, true, true),
                         new RecipeParameter(1, "保压时长", "s", 1, 0.5, 5, true, false)],
                        null, "UP-冲洗", "OP-Cyl 气缸保压", 22),
                    new RecipeStep(draftId, "S30", "qc", StepType.QualityCheck, 2, 200, 0, 30, null,
                        [new RecipeParameter(0, "硬度", "HB", 95, 90, 110, false, true)],
                        null, "UP-QC", Isa88.DefaultOperation(StepType.QualityCheck))
                ]);
            }

            await host.StartAsync();
            ProductionBatch? live = null;
            var deadline = DateTime.UtcNow.AddSeconds(25);
            do
            {
                await Task.Delay(200);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.Equal(BatchStatus.Completed, live?.Status);
            Assert.All(live!.StepExecutions, e => Assert.Equal(StepOutcome.Completed, e.Outcome));

            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Contains(log, e => e.StepCode == "S10" && e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_Type=21"));
                Assert.Contains(log, e => e.StepCode == "S10" && e.Kind == "trigger" && e.Detail != null && e.Detail.Contains("Trigger_Write=1"));
                Assert.Contains(log, e => e.StepCode == "S20" && e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_Type=22"));
                Assert.DoesNotContain(log, e => e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_Type=6"));
                Assert.DoesNotContain(log, e => e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_Type=5"));
                Assert.Contains(log, e => e.StepCode == "S30" && e.Kind == "quality" && e.Detail != null && e.Detail.Contains("禁止写 PLC"));
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_WaitHold_ResumesRemainingSeconds_DoesNotRewritePlc()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-waitleft-{Guid.NewGuid():N}.db");
        Guid batchId;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, _) = await SeedApprovedBatchAsync(db, "HT-WL", "ITWL", "BITWL", draftId =>
                [
                    new RecipeStep(draftId, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                        [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                         new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)]),
                    new RecipeStep(draftId, "S20", "wait", StepType.Wait, 1, 100, 0, 30, null,
                        [new RecipeParameter(0, "等待时长", "s", 8, 1, 60, false, false)])
                ]);
            }

            await host.StartAsync();
            var scheduler = host.Services.GetRequiredService<IBatchScheduler>();

            var waitHost = DateTime.UtcNow.AddSeconds(12);
            List<HandshakeEvent> log;
            do
            {
                await Task.Delay(120);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
            } while (!log.Any(e => e.StepCode == "S20" && e.Kind == "wait") && DateTime.UtcNow < waitHost);

            Assert.Contains(log, e => e.StepCode == "S20" && e.Kind == "wait");
            await Task.Delay(2500);
            await scheduler.EnqueueHoldAsync(batchId, "等待中保持剩余");

            ProductionBatch? live = null;
            var waitHeld = DateTime.UtcNow.AddSeconds(8);
            do
            {
                await Task.Delay(150);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: not BatchStatus.Held } && DateTime.UtcNow < waitHeld);

            Assert.Equal(BatchStatus.Held, live?.Status);
            using (var holdScope = host.Services.CreateScope())
            {
                var db = holdScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var hold = (await db.HandshakeEvents.AsNoTracking()
                    .Where(e => e.BatchId == batchId && e.Kind == "hold" && e.Phase == "HostWait")
                    .ToListAsync())
                    .OrderByDescending(e => e.CreatedAt)
                    .First();
                Assert.NotNull(hold.RemainingSeconds);
                Assert.InRange(hold.RemainingSeconds!.Value, 2.5, 7.5);
            }

            using (var resumeScope = host.Services.CreateScope())
            {
                var db = resumeScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var tracked = await db.Batches.SingleAsync(b => b.Id == batchId);
                tracked.Resume();
                await db.SaveChangesAsync();
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await scheduler.EnqueueStartAsync(batchId);
            var deadline = DateTime.UtcNow.AddSeconds(12);
            do
            {
                await Task.Delay(150);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.Equal(BatchStatus.Completed, live?.Status);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(7.2), "恢复应续跑剩余，不应整段重跑 8s");
            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var rows = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.DoesNotContain(rows, e => e.StepCode == "S20" && e.Kind == "write");
                Assert.Contains(rows, e => e.StepCode == "S20" && e.Kind == "wait" && e.Detail != null && e.Detail.Contains("恢复等待剩余"));
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_RecoverRunning_HostWaitContinuesRemaining_DoesNotRewriteCompletedHeat()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-recover-wait-{Guid.NewGuid():N}.db");
        Guid batchId;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, _) = await SeedApprovedBatchAsync(db, "HT-RV", "ITRV", "BITRV", draftId =>
                [
                    new RecipeStep(draftId, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                        [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                         new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)]),
                    new RecipeStep(draftId, "S20", "wait", StepType.Wait, 1, 100, 0, 30, null,
                        [new RecipeParameter(0, "等待时长", "s", 12, 1, 60, false, false)])
                ]);
            }

            await host.StartAsync();
            var waitHost = DateTime.UtcNow.AddSeconds(15);
            List<HandshakeEvent> log;
            do
            {
                await Task.Delay(120);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
            } while (!log.Any(e => e.StepCode == "S20" && e.Kind == "wait") && DateTime.UtcNow < waitHost);

            Assert.Contains(log, e => e.StepCode == "S20" && e.Kind == "wait");
            Assert.Contains(log, e => e.StepCode == "S10" && e.Kind == "write");
            await Task.Delay(2500);
            await host.StopAsync(TimeSpan.FromSeconds(5));
            host.Dispose();

            var recovered = new ConcurrentBag<ExecutionEvent>();
            host = CreateHost(dbPath, recovered);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await host.StartAsync();

            ProductionBatch? live = null;
            var deadline = DateTime.UtcNow.AddSeconds(14);
            do
            {
                await Task.Delay(150);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.Equal(BatchStatus.Completed, live?.Status);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(11.5), "引擎恢复应续跑剩余等待，不应整段重跑 12s");

            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var rows = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Equal(1, rows.Count(e => e.StepCode == "S10" && e.Kind == "write"));
                Assert.DoesNotContain(rows, e => e.StepCode == "S20" && e.Kind == "write");
                var recoverWait = rows
                    .Where(e => e.StepCode == "S20" && e.Kind == "wait" && e.Detail != null && e.Detail.Contains("引擎恢复等待剩余"))
                    .OrderByDescending(e => e.CreatedAt)
                    .FirstOrDefault();
                Assert.NotNull(recoverWait);
                Assert.NotNull(recoverWait!.RemainingSeconds);
                Assert.True(recoverWait.RemainingSeconds < 11.5);
            }
        }
        finally
        {
            try { await host.StopAsync(TimeSpan.FromSeconds(3)); } catch { /* already stopped */ }
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_RecoverRunning_StepRunning_DoesNotRewritePlcPayload()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-recover-heat-{Guid.NewGuid():N}.db");
        Guid batchId;
        var rack = new SimulatedPlcRack();
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events, rack);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, _) = await SeedApprovedBatchAsync(db, "HT-RH", "ITRH", "BITRH", draftId =>
                [
                    new RecipeStep(draftId, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                        [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                         new RecipeParameter(1, "时长", "s", 8, 1, 60, true, true)])
                ]);
            }

            await host.StartAsync();
            var waitRun = DateTime.UtcNow.AddSeconds(12);
            List<HandshakeEvent> log;
            do
            {
                await Task.Delay(120);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
            } while (!log.Any(e => e.StepCode == "S10" && e.Kind == "write") && DateTime.UtcNow < waitRun);

            Assert.Contains(log, e => e.StepCode == "S10" && e.Kind == "write");
            await Task.Delay(600);
            await host.StopAsync(TimeSpan.FromSeconds(5));
            host.Dispose();

            host = CreateHost(dbPath, new ConcurrentBag<ExecutionEvent>(), rack);
            await host.StartAsync();

            ProductionBatch? live = null;
            var deadline = DateTime.UtcNow.AddSeconds(14);
            do
            {
                await Task.Delay(150);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.Equal(BatchStatus.Completed, live?.Status);
            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var rows = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Equal(1, rows.Count(e => e.StepCode == "S10" && e.Kind == "write"));
                Assert.Contains(rows, e => e.Kind == "resume" && e.Detail != null && e.Detail.Contains("引擎恢复"));
            }
        }
        finally
        {
            try { await host.StopAsync(TimeSpan.FromSeconds(3)); } catch { /* already stopped */ }
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task Scheduler_RecoverRunning_ReplaysPersistedHold()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-recover-hold-{Guid.NewGuid():N}.db");
        Guid batchId;
        Guid equipmentId;
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = CreateHost(dbPath, events);

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, equipmentId) = await SeedTwoStepBatchAsync(db, "HT-PH", "ITPH", "BITPHLD");
                db.SchedulerIntents.Add(new SchedulerIntent(batchId, SchedulerIntentKinds.Hold, "重启后仍保持"));
                await db.SaveChangesAsync();
            }

            host.Services.GetRequiredService<SimulatedPlcRack>().Get(equipmentId).InjectFault("HoldNotReady");
            await host.StartAsync();
            host.Services.GetRequiredService<SimulatedPlcRack>().Get(equipmentId).InjectFault("None");

            ProductionBatch? live = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            do
            {
                await Task.Delay(200);
                using var poll = host.Services.CreateScope();
                var db = poll.ServiceProvider.GetRequiredService<AppDbContext>();
                live = await db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
            } while (live is { Status: BatchStatus.Queued or BatchStatus.Running } && DateTime.UtcNow < deadline);

            Assert.Equal(BatchStatus.Held, live?.Status);
        }
        finally
        {
            try { await host.StopAsync(TimeSpan.FromSeconds(3)); } catch { /* already stopped */ }
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }
}
