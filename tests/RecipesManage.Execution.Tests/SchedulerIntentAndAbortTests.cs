using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using RecipesManage.Infrastructure.Plc;
using Xunit;
using static RecipesManage.Execution.Tests.SchedulerHarness;
using RecipesManage.Simulation;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 调度意图按工步隔离、消费与删除同一次落库，以及中止时"停会话 → 复位 → 放租约"的顺序。
/// </summary>
public sealed class SchedulerIntentAndAbortTests
{
    [Fact]
    public async Task ParallelConfirms_OnTwoLanes_BothAdvance()
    {
        var dbPath = NewDbPath("brmes-par-confirm");
        var fake = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = CreateHost(dbPath, new ConcurrentBag<ExecutionEvent>(), clock: fake);
        try
        {
            var seeded = await SeedParallelConfirmAsync(host, "BPCF1");
            await host.StartAsync();
            using var driver = DriveTime(fake);
            await WaitBothAwaitingAsync(host, seeded.BatchId);

            await WriteIntentAsync(host, seeded.BatchId, seeded.StepA, "确认 A");
            await WriteIntentAsync(host, seeded.BatchId, seeded.StepB, "确认 B");
            var scheduler = host.Services.GetRequiredService<IBatchScheduler>();
            // 背靠背两条：按批次一格存的话，B 会覆盖 A，A 那条车道永远等不到确认。
            await scheduler.EnqueueConfirmAsync(seeded.BatchId, "确认 A", seeded.StepA);
            await scheduler.EnqueueConfirmAsync(seeded.BatchId, "确认 B", seeded.StepB);

            var live = await WaitUntilAsync(host, seeded.BatchId,
                b => b is { Status: BatchStatus.Completed },
                sp => ReadBatchAsync(sp, seeded.BatchId), timeoutMs: 20_000);

            Assert.True(live?.Status == BatchStatus.Completed, Describe(live));
            Assert.All(live!.StepExecutions, e => Assert.Equal(StepOutcome.Completed, e.Outcome));
            Assert.Empty(await ReadIntentsAsync(host, seeded.BatchId));
        }
        finally
        {
            DisposeHost(dbPath, host);
        }
    }

    [Fact]
    public async Task ConsumingOneConfirm_DeletesOnlyItsOwnIntent_InTheSameCommit()
    {
        var dbPath = NewDbPath("brmes-intent-row");
        var fake = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = CreateHost(dbPath, new ConcurrentBag<ExecutionEvent>(), clock: fake);
        try
        {
            var seeded = await SeedParallelConfirmAsync(host, "BPCF2");
            await host.StartAsync();
            using var driver = DriveTime(fake);
            await WaitBothAwaitingAsync(host, seeded.BatchId);

            await WriteIntentAsync(host, seeded.BatchId, seeded.StepA, "确认 A");
            // B 的行已经签名落库，但还没送进调度器：A 被消费时不能顺手删掉它。
            await WriteIntentAsync(host, seeded.BatchId, seeded.StepB, "确认 B");
            var scheduler = host.Services.GetRequiredService<IBatchScheduler>();
            await scheduler.EnqueueConfirmAsync(seeded.BatchId, "确认 A", seeded.StepA);

            var afterA = await WaitUntilAsync(host, seeded.BatchId,
                b => b?.StepExecutions.Single(s => s.StepId == seeded.StepA).Outcome == StepOutcome.Completed,
                sp => ReadBatchAsync(sp, seeded.BatchId));
            Assert.Equal(StepOutcome.Completed, afterA!.StepExecutions.Single(s => s.StepId == seeded.StepA).Outcome);

            // 工步结论与意图删除同一次落库：看得见 A 完成时，A 的行必须已经不在，B 的行必须还在。
            var remaining = await ReadIntentsAsync(host, seeded.BatchId);
            var only = Assert.Single(remaining);
            Assert.Equal(seeded.StepB, only.StepId);

            await scheduler.EnqueueConfirmAsync(seeded.BatchId, "确认 B", seeded.StepB);
            var done = await WaitUntilAsync(host, seeded.BatchId,
                b => b is { Status: BatchStatus.Completed },
                sp => ReadBatchAsync(sp, seeded.BatchId), timeoutMs: 20_000);
            Assert.True(done?.Status == BatchStatus.Completed, Describe(done));
            Assert.Empty(await ReadIntentsAsync(host, seeded.BatchId));
        }
        finally
        {
            DisposeHost(dbPath, host);
        }
    }

    /// <summary>
    /// 阶段 4 验收："写意图 → 重启 → 恢复"。确认意图在进程停机前已签名落库、但内存里的请求丢了：
    /// 新进程起来后，调度器必须从库里把它们找回来，两条车道照常推进，意图随消费删除。
    /// </summary>
    [Fact]
    public async Task Restart_ReplaysPersistedConfirms_WithoutAnyEnqueue()
    {
        var dbPath = NewDbPath("brmes-replay-confirm");
        var fake = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = CreateHost(dbPath, new ConcurrentBag<ExecutionEvent>(), clock: fake);
        try
        {
            var seeded = await SeedParallelConfirmAsync(host, "BRPC1");
            await WriteIntentAsync(host, seeded.BatchId, seeded.StepA, "确认 A");
            await WriteIntentAsync(host, seeded.BatchId, seeded.StepB, "确认 B");
            await host.StartAsync();
            using var driver = DriveTime(fake);   // 没有任何 EnqueueConfirm：只有库里的两行

            var done = await WaitUntilAsync(host, seeded.BatchId,
                b => b is { Status: BatchStatus.Completed },
                sp => ReadBatchAsync(sp, seeded.BatchId), timeoutMs: 20_000);

            Assert.True(done?.Status == BatchStatus.Completed, Describe(done));
            Assert.Empty(await ReadIntentsAsync(host, seeded.BatchId));
        }
        finally
        {
            DisposeHost(dbPath, host);
        }
    }

    [Fact]
    public async Task Restart_ReplaysPersistedSkip_ForTheNamedStepOnly()
    {
        var dbPath = NewDbPath("brmes-replay-skip");
        var fake = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = CreateHost(dbPath, new ConcurrentBag<ExecutionEvent>(), clock: fake);
        try
        {
            var seeded = await SeedParallelConfirmAsync(host, "BRPS1");
            await WriteIntentAsync(host, seeded.BatchId, seeded.StepA, "跳过 A", SchedulerIntentKinds.Skip);
            await WriteIntentAsync(host, seeded.BatchId, seeded.StepB, "确认 B");
            await host.StartAsync();
            using var driver = DriveTime(fake);

            var done = await WaitUntilAsync(host, seeded.BatchId,
                b => b is { Status: BatchStatus.Completed },
                sp => ReadBatchAsync(sp, seeded.BatchId), timeoutMs: 20_000);

            Assert.True(done?.Status == BatchStatus.Completed, Describe(done));
            Assert.Equal(StepOutcome.Skipped, done!.StepExecutions.Single(s => s.StepId == seeded.StepA).Outcome);
            Assert.Equal(StepOutcome.Completed, done.StepExecutions.Single(s => s.StepId == seeded.StepB).Outcome);
            Assert.Empty(await ReadIntentsAsync(host, seeded.BatchId));
        }
        finally
        {
            DisposeHost(dbPath, host);
        }
    }

    [Fact]
    public async Task Abort_WhileStepRunning_IdlesPlcBeforeReleasingLease()
    {
        var dbPath = NewDbPath("brmes-abort-order");
        var fake = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var rack = new SimulatedPlcRack(fake);
        var host = CreateHost(dbPath, new ConcurrentBag<ExecutionEvent>(), rack, clock: fake);
        try
        {
            Guid batchId;
            Guid equipmentId;
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, equipmentId) = await SeedApprovedBatchAsync(db, "HT-AB", "ITAB", "BITABORT", draftId =>
                [
                    Step(draftId, "S10", "heat", StepType.Heat, 0,
                        new Param("目标温度", "℃", 120, 100, 200), new Param("时长", "s", 5, 0.5, 5))
                ]);
                var batch = await db.Batches.SingleAsync(b => b.Id == batchId);
                db.EquipmentLeases.Add(new EquipmentLease(equipmentId, "HT-AB", batchId, batch.BatchNo, DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
            }

            await host.StartAsync();
            using var driver = DriveTime(fake);
            var station = rack.Get(equipmentId);
            var running = await WaitUntilAsync(host, batchId,
                running => running,
                _ => Task.FromResult(station.ReadSignals().StepRunning));
            Assert.True(running);

            // 与 BatchService.AbortAsync 同样的落库：批次先定稿为 Aborted，租约留给调度器放。
            // 调度器每个 tick 都在写这一行：乐观并发下操作员那次写可能撞车。生产里 BatchService 把它翻成
            // 409 CONFLICT「请刷新后重试」，由操作员再点一次——这里照做，而不是把一次撞车当成测试失败。
            for (var attempt = 1; ; attempt++)
            {
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var batch = await db.Batches.SingleAsync(b => b.Id == batchId);
                batch.Abort("集成测试中止");
                try
                {
                    await db.SaveChangesAsync();
                    break;
                }
                catch (DbUpdateConcurrencyException) when (attempt < 5)
                {
                }
            }
            await host.Services.GetRequiredService<IBatchScheduler>().EnqueueAbortAsync(batchId, "集成测试中止");

            var released = await WaitUntilAsync(host, batchId,
                gone => gone,
                async sp => !await sp.GetRequiredService<AppDbContext>().EquipmentLeases
                    .AnyAsync(l => l.EquipmentId == equipmentId),
                intervalMs: 10);
            var signalsAtRelease = station.ReadSignals();
            var eventsAtRelease = await CountEventsAsync(host, batchId);

            Assert.True(released, "调度器收尾后必须释放租约");
            Assert.False(signalsAtRelease.TriggerWriteEcho, "租约放出前握手位就必须已经复位");
            Assert.False(signalsAtRelease.HostHoldEcho);

            // 放租约之后会话不能再写履历：它在放租约之前就该停了。
            await Task.Delay(600);
            Assert.Equal(eventsAtRelease, await CountEventsAsync(host, batchId));
        }
        finally
        {
            DisposeHost(dbPath, host);
        }
    }

    /// <summary>失败时把批次的故障码与消息带进断言信息——"Faulted"本身看不出是谁把它置坏的。</summary>
    private static string Describe(ProductionBatch? b) =>
        $"status={b?.Status} fault={b?.FaultCode}: {b?.FaultMessage}";

    private sealed record ParallelConfirm(Guid BatchId, Guid StepA, Guid StepB);

    /// <summary>两台设备各一个人工确认工步并行，再汇合到一个质检工步。</summary>
    private static async Task<ParallelConfirm> SeedParallelConfirmAsync(IHost host, string batchNo)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await SchemaBootstrap.ApplyAsync(db);
        var a = new EquipmentLine($"{batchNo}-A", "unit A", PlcProtocol.Simulator, "127.0.0.1", 102,
            "S7_1200", 0, 1, SimulatorTagMap, "it");
        var b = new EquipmentLine($"{batchNo}-B", "unit B", PlcProtocol.Simulator, "127.0.0.1", 102,
            "S7_1200", 0, 1, SimulatorTagMap, "it");
        db.Equipment.AddRange(a, b);

        var recipe = MasterRecipe.Create($"IT-{batchNo}", "parallel confirm", "P", "part", null, Guid.NewGuid());
        var draft = recipe.RequireDraft();
        var s10 = new RecipeStep(draft.Id, "S10", "confirm A", StepType.ManualConfirm, 0, 0, 0, 30, null, [],
            unitProcedure: "UP-A");
        var s20 = new RecipeStep(draft.Id, "S20", "confirm B", StepType.ManualConfirm, 1, 100, 0, 30, null, [],
            unitProcedure: "UP-B");
        var s30 = new RecipeStep(draft.Id, "S30", "qc join", StepType.QualityCheck, 2, 50, 80, 30, null,
            [new RecipeParameter(0, "硬度", "HB", 95, 90, 110, false, false)],
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
        SnapshotIntegrity.Seal(snapshot, SnapshotJson.Options, out var json);
        var batch = ProductionBatch.Create(batchNo, a.Id, snapshot, json, Guid.NewGuid());
        foreach (var step in snapshot.Steps)
            batch.StepExecutions.Add(new BatchStepExecution(batch.Id, step.StepId, step.Code, step.Name, step.Type, step.Ordinal));
        batch.Queue();
        db.Batches.Add(batch);
        await db.SaveChangesAsync();
        return new ParallelConfirm(
            batch.Id,
            snapshot.Steps.Single(s => s.Code == "S10").StepId,
            snapshot.Steps.Single(s => s.Code == "S20").StepId);
    }

    private static async Task WaitBothAwaitingAsync(IHost host, Guid batchId)
    {
        static bool BothAwaiting(ProductionBatch? b) =>
            b is not null &&
            b.StepExecutions.Where(s => s.StepCode is "S10" or "S20")
                .All(s => s.Outcome == StepOutcome.AwaitingConfirm);

        var live = await WaitUntilAsync(host, batchId, BothAwaiting, sp => ReadBatchAsync(sp, batchId));
        Assert.True(BothAwaiting(live), "两条车道都应停在人工确认");
    }

    /// <summary>与 BatchService.ConfirmAsync 落的那一行同形：签名之后、入队之前先落库。</summary>
    private static async Task WriteIntentAsync(
        IHost host, Guid batchId, Guid stepId, string reason, string kind = SchedulerIntentKinds.Confirm)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SchedulerIntents.Add(new SchedulerIntent(batchId, kind, reason, stepId));
        await db.SaveChangesAsync();
    }

    private static async Task<List<SchedulerIntent>> ReadIntentsAsync(IHost host, Guid batchId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SchedulerIntents.AsNoTracking().Where(i => i.BatchId == batchId).ToListAsync();
    }

    private static async Task<int> CountEventsAsync(IHost host, Guid batchId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.HandshakeEvents.CountAsync(e => e.BatchId == batchId);
    }
}
