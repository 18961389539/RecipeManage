using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// </summary>
public sealed class ReadFailureToleranceTests
{
    private const string FastWatchdog = """{"readRetrySeconds":0.05,"readFailureToleranceSeconds":6}""";

    [Fact]
    public async Task AnOutageInsideTheToleranceWindow_DoesNotFaultTheBatch_AndNothingIsWrittenDuringIt()
    {
        var link = new PlcLink();
        var (dbPath, host, rack, batchId, equipmentId) = await ArrangeAsync("tol-ok", link, FastWatchdog);
        try
        {
            await host.StartAsync();
            var station = rack.Get(equipmentId);
            await WaitForAsync(() => station.ReadSignals().StepRunning, "工步进入 Running");

            link.Down = true;
            var writesAtCut = link.Writes;
            await Task.Delay(1_500);
            Assert.Equal(writesAtCut, link.Writes);
            link.Down = false;

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
        var (dbPath, host, rack, batchId, equipmentId) = await ArrangeAsync(
            "tol-lost", link, """{"readRetrySeconds":0.05,"readFailureToleranceSeconds":1}""");
        try
        {
            await host.StartAsync();
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
        var (dbPath, host, _, batchId, _) = await ArrangeAsync("tol-config", link, FastWatchdog);
        try
        {
            link.Down = true;
            await host.StartAsync();
            var started = DateTime.UtcNow;
            var done = await WaitUntilAsync(host, batchId,
                b => b is { Status: BatchStatus.Completed or BatchStatus.Faulted },
                sp => ReadBatchAsync(sp, batchId), timeoutMs: 15_000);

            Assert.Equal(BatchStatus.Faulted, done?.Status);
            Assert.Equal("ENGINE", done?.FaultCode);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "不应等满容忍窗口");
        }
        finally
        {
            DisposeHost(dbPath, host);
        }
    }

    // ---- 装配 ----

    private static async Task<(string DbPath, Microsoft.Extensions.Hosting.IHost Host, SimulatedPlcRack Rack, Guid BatchId, Guid EquipmentId)>
        ArrangeAsync(string name, PlcLink link, string watchdogJson)
    {
        var dbPath = NewDbPath($"brmes-{name}");
        var rack = new SimulatedPlcRack();
        var host = CreateHost(dbPath, new ConcurrentBag<ExecutionEvent>(), rack, services =>
            services.AddSingleton<IPlcDriverFactory>(sp =>
                new FlakyDriverFactory(new PlcDriverFactory(sp.GetServices<IPlcDriverProvider>()), link)));

        Guid batchId, equipmentId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SchemaBootstrap.ApplyAsync(db);
            (batchId, equipmentId) = await SeedApprovedBatchAsync(db, "TL-01", "ITTL", "BTOL1", draftId =>
            [
                Step(draftId, "S10", "hold", StepType.Hold, 0,
                    new Param("保温温度", "℃", 120, 100, 200), new Param("保温时长", "s", 4, 0.5, 5))
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
