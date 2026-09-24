using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using RecipesManage.Infrastructure.Plc;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 「四个等待环 × 操作员请求」的落地语义矩阵。
///
/// 引擎里 PLC 握手环、上位机等待环、人工确认环各自复制了一遍"停 / 跳 / 保 / 存 / 广播 / 返回"六段式
/// （连邻道让步分支共七份）。这个矩阵钉住的就是这七份必须表现一致：同一种请求落在同一个相位上，
/// 工步结局、批次状态、车道相位、握手履历、实时事件五样都对得上。
/// 抽公共实现之前先跑一遍，抽完再跑一遍，两遍结论必须一字不差。
///
/// 另有一处**刻意的不对称**单独钉住，见 <see cref="SkipDuringPlcRunning_IsNotConsumed"/>。
/// </summary>
public sealed class PhaseGateMatrixTests
{
    public sealed record Case(
        string Label,
        StepType Type,
        string Phase,
        string Request,
        string FaultMode,
        double DurationSeconds,
        StepOutcome ExpectedOutcome,
        string ExpectedDetail);

    /// <summary>保持的原因统一，跳步按相位给可区分的文案（要能在履历里认出是哪条路径）。</summary>
    private static string ReasonFor(Case c) => c.Request == "hold"
        ? "运行中保持"
        : c.Phase switch
        {
            "HostWait" => "等待中跳步",
            "AwaitingConfirm" => "确认前跳步",
            _ => "就绪前跳步"
        };

    public static TheoryData<Case> Cases => new()
    {
        new Case("plc-running-hold", StepType.Heat, "StepRunning", "hold", "", 8,
            StepOutcome.Held, "Host_Hold"),
        new Case("plc-ready-hold", StepType.Heat, "WaitingPlcReady", "hold", "HoldNotReady", 1,
            StepOutcome.Pending, "运行中保持"),
        new Case("hostwait-hold", StepType.Wait, "HostWait", "hold", "", 20,
            StepOutcome.Held, "剩余"),
        new Case("confirm-hold", StepType.ManualConfirm, "AwaitingConfirm", "hold", "", 1,
            StepOutcome.Held, "运行中保持"),
        new Case("plc-ready-skip", StepType.Heat, "WaitingPlcReady", "skip", "HoldNotReady", 1,
            StepOutcome.Skipped, "就绪前跳步"),
        new Case("hostwait-skip", StepType.Wait, "HostWait", "skip", "", 20,
            StepOutcome.Skipped, "等待中跳步"),
        new Case("confirm-skip", StepType.ManualConfirm, "AwaitingConfirm", "skip", "", 1,
            StepOutcome.Skipped, "确认前跳步")
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Request_LandsWithTheSameShape_InEveryWaitLoop(Case c)
    {
        var dbPath = SchedulerHarness.NewDbPath("brmes-gate");
        var events = new ConcurrentBag<ExecutionEvent>();
        var rack = new SimulatedPlcRack();
        var host = SchedulerHarness.CreateHost(dbPath, events, rack);

        try
        {
            Guid batchId;
            Guid equipmentId;
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, equipmentId) = await SchedulerHarness.SeedApprovedBatchAsync(
                    db, $"HT-{c.Label}", $"ITG-{c.Label}", $"BGT-{c.Label}",
                    draftId =>
                    [
                        GateStep(draftId, "S10", c.Type, 0, c.DurationSeconds),
                        // 跳步之后还要走一步：证明 Skipped 真的放行了序列，而不是把批次卡住。
                        SchedulerHarness.Step(draftId, "S20", "hold", StepType.Hold, 1,
                            new SchedulerHarness.Param("保温温度", "℃", 120, 100, 200),
                            new SchedulerHarness.Param("保温时长", "s", 1, 0.5, 5))
                    ]);
            }

            if (c.FaultMode.Length > 0)
                rack.Get(equipmentId).InjectFault(c.FaultMode);

            await host.StartAsync();
            var scheduler = host.Services.GetRequiredService<IBatchScheduler>();

            var atPhase = await SchedulerHarness.WaitUntilAsync(
                host, batchId,
                b => b is not null && Reached(b.HandshakePhase, c.Phase),
                s => SchedulerHarness.ReadBatchAsync(s, batchId));
            Assert.NotNull(atPhase);
            Assert.True(Reached(atPhase!.HandshakePhase, c.Phase),
                $"没等到相位 {c.Phase}，实际 {atPhase.HandshakePhase}");
            Assert.Equal(BatchStatus.Running, atPhase.Status);

            var reason = ReasonFor(c);
            var requestedAt = DateTime.UtcNow;
            if (c.Request == "hold")
                await scheduler.EnqueueHoldAsync(batchId, reason);
            else
                await scheduler.EnqueueSkipAsync(batchId, reason);

            // 第一段只等"这一相的结局"，不等批次收尾。
            var settled = await SchedulerHarness.WaitUntilAsync(
                host, batchId,
                b => b is not null
                    && b.StepExecutions.Single(e => e.StepCode == "S10").Outcome == c.ExpectedOutcome
                    && (c.Request != "hold" || b.Status == BatchStatus.Held),
                s => SchedulerHarness.ReadBatchAsync(s, batchId),
                timeoutMs: 8_000);

            Assert.NotNull(settled);
            var took = DateTime.UtcNow - requestedAt;
            Assert.Equal(c.ExpectedOutcome, settled!.StepExecutions.Single(e => e.StepCode == "S10").Outcome);

            if (c.Request == "hold")
            {
                // 保持是"整批立刻暂停"：不能等这一相跑完才生效（运行中保持靠 Host_Hold 应答）。
                Assert.True(took < TimeSpan.FromSeconds(6), $"保持花了 {took.TotalSeconds:0.#}s 才生效。");
                Assert.Equal(BatchStatus.Held, settled.Status);
                Assert.Equal("Held", settled.HandshakePhase);
                Assert.Equal(StepOutcome.Pending, settled.StepExecutions.Single(e => e.StepCode == "S20").Outcome);
            }

            if (c.Request == "skip")
            {
                // 就绪前跳步时仿真站还挂着 HoldNotReady，不清掉下一步永远跑不完。
                if (c.FaultMode.Length > 0)
                    rack.Get(equipmentId).InjectFault("None");
                var done = await SchedulerHarness.WaitUntilAsync(
                    host, batchId,
                    b => b is not null && b.Status is BatchStatus.Completed or BatchStatus.Faulted,
                    s => SchedulerHarness.ReadBatchAsync(s, batchId),
                    timeoutMs: 25_000);
                Assert.NotNull(done);
                Assert.Equal(BatchStatus.Completed, done!.Status);
                Assert.Equal(StepOutcome.Completed, done.StepExecutions.Single(e => e.StepCode == "S20").Outcome);
            }

            List<HandshakeEvent> log;
            using (var logScope = host.Services.CreateScope())
                log = await SchedulerHarness.ReadHandshakeLogAsync(logScope.ServiceProvider, batchId);

            var gateEvent = Assert.Single(log, e => e.Kind == c.Request && e.StepCode == "S10");
            Assert.NotNull(gateEvent.Detail);
            Assert.Contains(c.ExpectedDetail, gateEvent.Detail);
            // 履历相位必须就是请求落下时的相位：六段式里的停/保/跳没走错分支。
            Assert.Equal(c.Phase, gateEvent.Phase);

            Assert.Contains(events, e => e.Type == (c.Request == "hold" ? "held" : "step"));
            Assert.DoesNotContain(events, e => e.Type == "alarm");
        }
        finally
        {
            SchedulerHarness.DisposeHost(dbPath, host);
        }
    }

    /// <summary>
    /// PLC 已经在跑一个相（StepRunning）时，跳步意图**不被消费**——操作员那时只能保持。
    /// 但意图不丢：要到下一个可跳点（S20 的就绪相位）才生效。
    /// 这条不对称是安全语义，不是复制漏了，单独钉住，免得抽公共实现时"顺手统一"掉。
    /// </summary>
    [Fact]
    public async Task SkipDuringPlcRunning_IsNotConsumed()
    {
        var dbPath = SchedulerHarness.NewDbPath("brmes-runskip");
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = SchedulerHarness.CreateHost(dbPath, events);

        try
        {
            Guid batchId;
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, _) = await SchedulerHarness.SeedApprovedBatchAsync(
                    db, "HT-RS", "ITG-RS", "BGT-RS",
                    draftId =>
                    [
                        GateStep(draftId, "S10", StepType.Heat, 0, 8),
                        SchedulerHarness.Step(draftId, "S20", "hold", StepType.Hold, 1,
                            new SchedulerHarness.Param("保温温度", "℃", 120, 100, 200),
                            new SchedulerHarness.Param("保温时长", "s", 1, 0.5, 5))
                    ]);
            }

            await host.StartAsync();
            var scheduler = host.Services.GetRequiredService<IBatchScheduler>();

            var running = await SchedulerHarness.WaitUntilAsync(
                host, batchId,
                b => b is not null && Reached(b.HandshakePhase, "StepRunning"),
                s => SchedulerHarness.ReadBatchAsync(s, batchId));
            Assert.NotNull(running);
            Assert.True(Reached(running!.HandshakePhase, "StepRunning"), running.HandshakePhase);

            await scheduler.EnqueueSkipAsync(batchId, "运行中不该被跳掉");
            await Task.Delay(600);

            using (var check = host.Services.CreateScope())
            {
                var live = await SchedulerHarness.ReadBatchAsync(check.ServiceProvider, batchId);
                Assert.Equal(StepOutcome.Running, live!.StepExecutions.Single(e => e.StepCode == "S10").Outcome);
                Assert.True(Reached(live.HandshakePhase, "StepRunning"),
                    $"运行中的跳步不该改变相位：{live.HandshakePhase}");
            }

            List<HandshakeEvent> log;
            using (var logScope = host.Services.CreateScope())
                log = await SchedulerHarness.ReadHandshakeLogAsync(logScope.ServiceProvider, batchId);
            Assert.DoesNotContain(log, e => e.Kind == "skip" && e.StepCode == "S10");

            // 意图不丢：等到下一个可跳点（S20 的就绪相位）才生效。
            var done = await SchedulerHarness.WaitUntilAsync(
                host, batchId,
                b => b is not null && b.Status is BatchStatus.Completed or BatchStatus.Faulted,
                s => SchedulerHarness.ReadBatchAsync(s, batchId),
                timeoutMs: 30_000);
            Assert.NotNull(done);
            Assert.Equal(BatchStatus.Completed, done!.Status);
            Assert.Equal(StepOutcome.Completed, done.StepExecutions.Single(e => e.StepCode == "S10").Outcome);
            Assert.Equal(StepOutcome.Skipped, done.StepExecutions.Single(e => e.StepCode == "S20").Outcome);

            using (var logScope = host.Services.CreateScope())
                log = await SchedulerHarness.ReadHandshakeLogAsync(logScope.ServiceProvider, batchId);
            var skip = Assert.Single(log, e => e.Kind == "skip");
            Assert.Equal("S20", skip.StepCode);
            Assert.Equal("WaitingPlcReady", skip.Phase);
        }
        finally
        {
            SchedulerHarness.DisposeHost(dbPath, host);
        }
    }

    /// <summary>被试工步：写 PLC 的相带"时长"把相位拖住，上位机两类按各自口径给参数。</summary>
    private static RecipeStep GateStep(Guid draftId, string code, StepType type, int ordinal, double durationSeconds) =>
        type switch
        {
            StepType.Wait => SchedulerHarness.Step(draftId, code, "wait", StepType.Wait, ordinal,
                new SchedulerHarness.Param("等待时长", "s", durationSeconds, 0.5, 3600)),
            StepType.ManualConfirm => SchedulerHarness.Step(draftId, code, "confirm", StepType.ManualConfirm, ordinal,
                new SchedulerHarness.Param("确认意见", "", 0, 0, 0)),
            _ => SchedulerHarness.Step(draftId, code, "heat", StepType.Heat, ordinal,
                new SchedulerHarness.Param("目标温度", "℃", 120, 100, 200),
                new SchedulerHarness.Param("时长", "s", durationSeconds, 0.5, 60))
        };

    /// <summary>单车道时批次汇总相位就是车道相位本身；多车道才带 "CODE:" 前缀。</summary>
    private static bool Reached(string merged, string phase) =>
        merged == phase || merged.Contains($":{phase}", StringComparison.Ordinal);
}
