using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Batches;
using RecipesManage.Infrastructure.Persistence;
using RecipesManage.Simulation;
using Xunit;
using static RecipesManage.Execution.Tests.SchedulerHarness;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 设备上残留着上一批（或崩溃前那一步）的握手位时，一个从没发过任何指令的新工步不能把它当成自己的进度。
///
/// 旧行为：会话的第一个工步无条件"从 PLC 当前相位续跑"。残留 Step_Complete 会让新工步直接进入归档、
/// 残留 Step_Running 会让它空等别人的时长——两种情况下新工步都被记成"完成"，而它的参数一次都没写给 PLC，
/// 批记录上却有完整的归档实测。续跑只在上位机自己记得"这一步发过指令"（工步已是 Running / Held）时才成立。
///
/// 残留从哪来：(a) 中止时 PLC 连不上，复位被吞掉，租约照放；(b) 崩在"工步已记完成"与"复位 Step_Complete"之间
/// （每步一个 100ms 量级的窗口）；(c) 现场有人手动动过设备。
/// </summary>
public sealed class StaleHandshakeTests
{
    [Fact]
    public async Task StaleStepComplete_FromAPreviousBatch_IsClearedAndTheNewStepsReallyRun()
    {
        var rack = new SimulatedPlcRack();
        await RunAgainstStaleDeviceAsync("stale-complete", rack, station =>
        {
            station.WritePayload(999, 1, new float[16]);
            station.SetTrigger(true);
        }, station => station.ReadSignals().StepComplete);
    }

    [Fact]
    public async Task StaleStepRunning_FromAPreviousBatch_IsClearedAndTheNewStepsReallyRun()
    {
        var rack = new SimulatedPlcRack();
        await RunAgainstStaleDeviceAsync("stale-running", rack, station =>
        {
            var parameters = new float[16];
            parameters[15] = 120;   // 工艺时长 120s：测试期间不会自己跑完
            station.WritePayload(999, 1, parameters);
            station.SetTrigger(true);
        }, station => station.ReadSignals().StepRunning);
    }

    private static async Task RunAgainstStaleDeviceAsync(
        string name,
        SimulatedPlcRack rack,
        Action<SimulatedPlcStation> leaveResidue,
        Func<SimulatedPlcStation, bool> residueVisible)
    {
        var dbPath = NewDbPath($"brmes-{name}");
        var host = CreateHost(dbPath, new ConcurrentBag<ExecutionEvent>(), rack);
        try
        {
            Guid batchId, equipmentId;
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SchemaBootstrap.ApplyAsync(db);
                (batchId, equipmentId) = await SeedTwoStepBatchAsync(db, "ST-01", "ITST", "BSTALE1");
            }

            var station = rack.Get(equipmentId);
            leaveResidue(station);
            Assert.True(
                await WaitUntilAsync(host, batchId, seen => seen, _ => Task.FromResult(residueVisible(station)),
                    timeoutMs: 6_000, intervalMs: 50),
                "前置条件：设备上要先有残留握手位");

            await host.StartAsync();
            var done = await WaitUntilAsync(host, batchId,
                b => b is { Status: BatchStatus.Completed or BatchStatus.Faulted },
                sp => ReadBatchAsync(sp, batchId), timeoutMs: 25_000);

            Assert.True(done?.Status == BatchStatus.Completed,
                $"status={done?.Status} fault={done?.FaultCode}: {done?.FaultMessage}");

            using var verify = host.Services.CreateScope();
            var log = await ReadHandshakeLogAsync(verify.ServiceProvider, batchId);
            foreach (var code in new[] { "S10", "S20" })
                Assert.True(
                    log.Any(e => e.StepCode == code && e.Kind == "write"),
                    $"{code} 必须真的把参数写给 PLC；没有 write 履历说明它是被残留位带着“完成”的");
        }
        finally
        {
            DisposeHost(dbPath, host);
        }
    }
}
