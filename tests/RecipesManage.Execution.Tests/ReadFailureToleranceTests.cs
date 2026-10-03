using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using RecipesManage.Infrastructure.Plc;
using RecipesManage.Simulation;
using Xunit;
using static RecipesManage.Execution.Tests.SchedulerHarness;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 工步进行中读 PLC 失败：窗口内只重连重读、不写任何信号；窗口用尽才报 PlcCommLost。
///
/// 旧行为是任何一次读异常（含 8 秒 IO 超时）直接把批次置成 ENGINE 故障，而 PLC 在自己的程序里还跑得好好的。
/// 红线是"禁止盲写"：断线期间一次写都不许发出去（用装饰器计数断言），恢复后只按最新读数继续。
///
/// 三个用例全部跑在 FakeTimeProvider 上：调度器的工步时长/读容忍窗都读引擎时钟，
/// 后台推进器把虚拟时钟推快约 6 倍——断线 1.5s、容忍窗 6s 这类窗口断言不再和
/// 机器负载赛跑（此前这组用例在高负载下随机挂，见 2026-10-03 复盘）。
/// </summary>
public sealed class ReadFailureToleranceTests
{
    private const string FastWatchdog = """{"readRetrySeconds":0.05,"readFailureToleranceSeconds":6}""";

    [Fact]
    public async Task AnOutageInsideTheToleranceWindow_DoesNotFaultTheBatch_AndNothingIsWrittenDuringIt()
    {
        var link = new PlcLink();
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (dbPath, host, rack, batchId, equipmentId) = await ArrangeAsync("tol-ok", link, FastWatchdog, fake);
        try
        {
            await host.StartAsync();
            // 推进器必须在等待 Running 之前启动：启动路径里的 Task.Delay 也挂在假时钟上，
            // 时钟不走启动流程就永远不会推进——先有鸡还是先有蛋。
            using var driver = DriveTime(fake);
            var station = rack.Get(equipmentId);
            await WaitForAsync(() => station.ReadSignals().StepRunning, "工步进入 Running");

            link.Down = true;
            driver.Pause();
            var writesAtCut = link.Writes;
            await driver.WaitVirtualAsync(TimeSpan.FromSeconds(1.5));
            Assert.Equal(writesAtCut, link.Writes);
            link.Down = false;
            driver.Resume();

            var done = await WaitUntilAsync(host, batchId,
                b => b is { Status: BatchStatus.Completed or BatchStatus.Faulted },
                sp => ReadBatchAsync(sp, batchId), timeoutMs: 25_000);
            Assert.True(done?.Status == BatchStatus.Completed,
                $"status={done?.Status} fault={done?.FaultCode}: {done?.FaultMessage}");

            Assert.Equal(0, link.WriteAttemptsWhileDown);
            Assert.Equal(1, link.PayloadWrites);   // 单工步：参数只写过一次，恢复后没有重写

            using var scope = host.Services.CreateScope();
            var log = await ReadHandshakeLogAsync(scope.ServiceProvider, batchId);
            Assert.Contains(log, e => e.Kind == "comm" && e.Detail!.Contains("进入通讯容忍窗口"));
            Assert.Contains(log, e => e.Kind == "comm" && e.Detail!.Contains("已恢复"));
        }
        finally
        {
            DisposeHost(dbPath, host);
        }
    }

    [Fact]
    public async Task AnOutageThatOutlastsTheWindow_FaultsAsPlcCommLost_WithoutEverWriting()
    {
        var link = new PlcLink();
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (dbPath, host, rack, batchId, equipmentId) = await ArrangeAsync(
            "tol-lost", link, """{"readRetrySeconds":0.05,"readFailureToleranceSeconds":1}""", fake);
        try
        {
            await host.StartAsync();
            using var driver = DriveTime(fake);
            var station = rack.Get(equipmentId);
            await WaitForAsync(() => station.ReadSignals().StepRunning, "工步进入 Running");

            link.Down = true;
            var done = await WaitUntilAsync(host, batchId,
                b => b is { Status: BatchStatus.Completed or BatchStatus.Faulted },
                sp => ReadBatchAsync(sp, batchId), timeoutMs: 20_000);

            Assert.Equal(BatchStatus.Faulted, done?.Status);
            Assert.Equal(nameof(HandshakeFaultCode.PlcCommLost), done?.FaultCode);
            Assert.Contains("容忍窗口", done?.FaultMessage);
            // 故障路径本身也不许往断线的 PLC 上补写。
            Assert.Equal(0, link.WriteAttemptsWhileDown);
            Assert.Equal(1, link.PayloadWrites);
        }
        finally
        {
            DisposeHost(dbPath, host);
        }
    }

    [Fact]
    public async Task ReadFailuresThatAreNotLinkProblems_AreNotWaitedOut()
    {
        // 配置/代码错误（这里用 NotSupportedException 代表）等多久都不会好，不进容忍窗口，仍按引擎故障立刻暴露。
        var link = new PlcLink { Failure = () => new NotSupportedException("点表协议未支持") };
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (dbPath, host, _, batchId, _) = await ArrangeAsync("tol-config", link, FastWatchdog, fake);
        try
        {
            using var driver = DriveTime(fake);
            link.Down = true;
            await host.StartAsync();
            var done = await WaitUntilAsync(host, batchId,
                b => b is { Status: BatchStatus.Completed or BatchStatus.Faulted },
                sp => ReadBatchAsync(sp, batchId), timeoutMs: 15_000);

            Assert.Equal(BatchStatus.Faulted, done?.Status);
            Assert.Equal("ENGINE", done?.FaultCode);
            // 时序断言在虚拟时钟下已无意义（6s 容忍窗虚拟上几毫秒真实就走完）：
            // 真正的契约是 FaultCode=ENGINE 而不是 PlcCommLost——不进容忍窗。
            Assert.DoesNotContain("容忍窗口", done?.FaultMessage);
        }
        finally
        {
            DisposeHost(dbPath, host);
        }
    }

    // ---- 装配 ----

    private static async Task<(string DbPath, Microsoft.Extensions.Hosting.IHost Host, SimulatedPlcRack Rack, Guid BatchId, Guid EquipmentId)>
        ArrangeAsync(string name, PlcLink link, string watchdogJson, TimeProvider? clock = null)
    {
        var dbPath = NewDbPath($"brmes-{name}");
        var rack = new SimulatedPlcRack(clock);
        var host = CreateHost(dbPath, new ConcurrentBag<ExecutionEvent>(), rack, services =>
            services.AddSingleton<IPlcDriverFactory>(sp =>
                new FlakyDriverFactory(new PlcDriverFactory(sp.GetServices<IPlcDriverProvider>()), link)), clock);

        Guid batchId, equipmentId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SchemaBootstrap.ApplyAsync(db);
            (batchId, equipmentId) = await SeedApprovedBatchAsync(db, "TL-01", "ITTL", "BTOL1", draftId =>
            [
                // 保温 12s（虚拟）：6.7 倍速下 Running 位有约 1.8s 真实窗口可被观察到，
                // 4s 的话整步 600ms 真实就跑完了，负载下 WaitForAsync 会错过 Running。
                Step(draftId, "S10", "hold", StepType.Hold, 0,
                    new Param("保温温度", "℃", 120, 100, 200), new Param("保温时长", "s", 12, 0.5, 30))
            ]);

            var eq = await db.Equipment.SingleAsync(e => e.Id == equipmentId);
            eq.Update(eq.Name, eq.Protocol, eq.Host, eq.Port, eq.PlcModel, eq.Rack, eq.Slot, eq.Enabled,
                eq.TagMapJson, eq.Description, watchdogJson);
            await db.SaveChangesAsync();
        }

        return (dbPath, host, rack, batchId, equipmentId);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"等待超时：{what}");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// 后台时钟推进器：每 15ms 真实时间推 100ms 虚拟时间（约 6.7 倍速）。
    /// 调度器的 Task.Delay 全挂在引擎时钟上，每次 Advance 会唤醒到期的循环——
    /// 工步时长与容忍窗口因此跑在虚拟时间里，断言不再和真实墙钟赛跑。
    ///
    /// 窗口关键期（断线保持 N 秒）要 Pause 后用 <see cref="WaitVirtualAsync"/> 手动推进：
    /// 推进器按真实速率走，调度器被真实 IO 卡住时虚拟时间仍在流逝，窗口可能被"撑爆"——
    /// 暂停后虚拟时间彻底静止，1.5s 的断线在 6s 的容忍窗里在数学上就不可能过期。
    /// </summary>
    private sealed class TimeDriver(FakeTimeProvider fake) : IDisposable
    {
        private readonly CancellationTokenSource cts = new();
        private volatile bool paused;

        public void Pause() => paused = true;
        public void Resume() => paused = false;

        public async Task WaitVirtualAsync(TimeSpan duration)
        {
            var target = fake.GetUtcNow() + duration;
            while (fake.GetUtcNow() < target)
            {
                fake.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(5);
            }
        }

        public void Dispose() => cts.Cancel();

        public Task Start() => Task.Run(RunAsync, CancellationToken.None);

        private async Task RunAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                if (!paused) fake.Advance(TimeSpan.FromMilliseconds(100));
                try { await Task.Delay(15, cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static TimeDriver DriveTime(FakeTimeProvider fake)
    {
        var driver = new TimeDriver(fake);
        driver.Start();
        return driver;
    }

    /// <summary>一条可以人为切断的线路；所有经它的 PLC 调用都在这里计数。</summary>
    private sealed class PlcLink
    {
        public volatile bool Down;
        public Func<Exception> Failure { get; init; } = () => new TimeoutException("模拟 IO 超时");
        private int _writes, _payloadWrites, _writeAttemptsWhileDown;
        public int Writes => Volatile.Read(ref _writes);
        public int PayloadWrites => Volatile.Read(ref _payloadWrites);
        public int WriteAttemptsWhileDown => Volatile.Read(ref _writeAttemptsWhileDown);

        public void BeforeRead()
        {
            if (Down)
                throw Failure();
        }

        public void BeforeWrite(bool payload)
        {
            if (Down)
            {
                Interlocked.Increment(ref _writeAttemptsWhileDown);
                throw Failure();
            }

            Interlocked.Increment(ref _writes);
            if (payload)
                Interlocked.Increment(ref _payloadWrites);
        }
    }

    private sealed class FlakyDriverFactory(IPlcDriverFactory inner, PlcLink link) : IPlcDriverFactory
    {
        public IPlcHandshakeClient Create(EquipmentLine equipment) => new FlakyClient(inner.Create(equipment), link);
    }

    private sealed class FlakyClient(IPlcHandshakeClient inner, PlcLink link) : IPlcHandshakeClient
    {
        public Task ConnectAsync(CancellationToken ct)
        {
            link.BeforeRead();
            return inner.ConnectAsync(ct);
        }

        public Task<PlcInboundSignals> ReadSignalsAsync(CancellationToken ct)
        {
            link.BeforeRead();
            return inner.ReadSignalsAsync(ct);
        }

        public Task<PlcStepPayload> ReadStepPayloadAsync(CancellationToken ct)
        {
            link.BeforeRead();
            return inner.ReadStepPayloadAsync(ct);
        }

        public Task<IReadOnlyDictionary<string, double>> ReadMeasuredAsync(CancellationToken ct)
        {
            link.BeforeRead();
            return inner.ReadMeasuredAsync(ct);
        }

        public Task WriteStepPayloadAsync(int stepId, int stepType, IReadOnlyList<float> parameters, CancellationToken ct)
        {
            link.BeforeWrite(payload: true);
            return inner.WriteStepPayloadAsync(stepId, stepType, parameters, ct);
        }

        public Task SetTriggerWriteAsync(bool value, CancellationToken ct)
        {
            link.BeforeWrite(payload: false);
            return inner.SetTriggerWriteAsync(value, ct);
        }

        public Task SetHostHoldAsync(bool value, CancellationToken ct)
        {
            link.BeforeWrite(payload: false);
            return inner.SetHostHoldAsync(value, ct);
        }

        public Task ResetCompleteAsync(CancellationToken ct)
        {
            link.BeforeWrite(payload: false);
            return inner.ResetCompleteAsync(ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
