using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using RecipesManage.Execution;
using RecipesManage.Infrastructure.Persistence;
using RecipesManage.Infrastructure.Plc;
using RecipesManage.Simulation;
using Xunit;
using static RecipesManage.Execution.Tests.SchedulerHarness;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// "欠下的设备复位"：中止时批次先定稿 Aborted、调度器稍后才复位设备。之间崩了、或 PLC 当时连不上，
/// Aborted 是终态，重启不会再碰它，设备上的握手位就一直留着，没有任何东西记得这笔账。
/// <see cref="PendingDeviceReset"/> 是那笔账的持久记录：随批次定稿同一次提交写入，复位确认成功才删。
/// </summary>
public sealed class PendingDeviceResetTests
{
    private const string Password = "Pending-Reset-1";

    // ------------------------------------------------------------ 写入端：BatchService

    [Fact]
    public async Task Abort_OfAStartedBatch_RecordsAPendingReset_InTheSameCommitAsTheAbort()
    {
        var (service, db, batchId, equipmentId) = await ArrangeBatchAsync("abort-started", started: true);

        await service.AbortAsync(batchId, "现场中止", Password, CancellationToken.None);

        var batch = await db.Batches.AsNoTracking().SingleAsync(b => b.Id == batchId);
        var marker = await db.PendingDeviceResets.AsNoTracking().SingleAsync();
        Assert.Equal(BatchStatus.Aborted, batch.Status);
        Assert.Equal(equipmentId, marker.EquipmentId);
        Assert.Equal(batchId, marker.BatchId);
    }

    [Fact]
    public async Task Abort_OfAQueuedBatch_OwesNothing_BecauseItNeverTouchedTheDevice()
    {
        var (service, db, batchId, _) = await ArrangeBatchAsync("abort-queued", started: false);

        await service.AbortAsync(batchId, "还没开始就中止", Password, CancellationToken.None);

        Assert.Empty(await db.PendingDeviceResets.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AbortingTwice_OnTheSameDevice_KeepsASingleRow()
    {
        var (service, db, batchId, equipmentId) = await ArrangeBatchAsync("abort-twice", started: true);
        // 上一次中止留下的账还没清
        db.PendingDeviceResets.Add(new PendingDeviceReset(equipmentId, Guid.NewGuid(), "BOLD", "旧账"));
        await db.SaveChangesAsync();

        await service.AbortAsync(batchId, "再中止一次", Password, CancellationToken.None);

        var marker = await db.PendingDeviceResets.AsNoTracking().SingleAsync();
        Assert.Equal(batchId, marker.BatchId);
    }

    // ------------------------------------------------------------ 补做端：调度器

    [Fact]
    public async Task Startup_ResetsAnOrphanedDevice_AndClearsTheMarker()
    {
        var rack = new SimulatedPlcRack();
        var dbPath = NewDbPath("brmes-orphan-reset");
        var host = CreateHost(dbPath, new ConcurrentBag<ExecutionEvent>(), rack);
        try
        {
            var equipmentId = await SeedOrphanAsync(host, rack, "ORPHAN-1");
            var station = rack.Get(equipmentId);
            Assert.True(station.ReadSignals().StepRunning, "前置条件：设备上残留着 Step_Running");

            await host.StartAsync();

            var idle = await WaitUntilAsync(host, Guid.Empty, done => done,
                _ => Task.FromResult(!station.ReadSignals().StepRunning && !station.ReadSignals().TriggerWriteEcho),
                timeoutMs: 8_000, intervalMs: 50);
            Assert.True(idle, "启动后调度器必须把孤儿设备的握手位复位");

            Assert.True(await WaitUntilAsync(host, Guid.Empty, done => done,
                async sp => !await sp.GetRequiredService<AppDbContext>().PendingDeviceResets.AnyAsync(),
                timeoutMs: 5_000, intervalMs: 50), "复位确认成功后标记必须删除");

            using var scope = host.Services.CreateScope();
            var audit = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs.AsNoTracking()
                .SingleAsync(a => a.Action == "device.reset");
            Assert.Equal(equipmentId.ToString(), audit.EntityId);
        }
        finally
        {
            DisposeHost(dbPath, host);
        }
    }

    [Fact]
    public async Task MarkerForADeviceNowOwnedByAnotherBatch_IsDropped_WithoutTouchingTheDevice()
    {
        var rack = new SimulatedPlcRack();
        var dbPath = NewDbPath("brmes-orphan-owned");
        var host = CreateHost(dbPath, new ConcurrentBag<ExecutionEvent>(), rack);
        try
        {
            var equipmentId = await SeedOrphanAsync(host, rack, "ORPHAN-2");
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // 另一个批次现在占着这台设备：往上写复位会打断它的握手。
                db.EquipmentLeases.Add(new EquipmentLease(equipmentId, "ORPHAN-2", Guid.NewGuid(), "BNEW", DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
            }

            await host.StartAsync();

            Assert.True(await WaitUntilAsync(host, Guid.Empty, done => done,
                async sp => !await sp.GetRequiredService<AppDbContext>().PendingDeviceResets.AnyAsync(),
                timeoutMs: 5_000, intervalMs: 50), "被占用的设备，旧账作废");
            Assert.True(rack.Get(equipmentId).ReadSignals().StepRunning, "设备不能被动：那是别的批次的现场");
        }
        finally
        {
            DisposeHost(dbPath, host);
        }
    }

    [Fact]
    public async Task UnreachableDevice_KeepsItsMarker_AndIsResetOnceTheLinkComesBack()
    {
        var rack = new SimulatedPlcRack();
        var link = new LinkSwitch { Down = true };
        var dbPath = NewDbPath("brmes-orphan-retry");
        var host = CreateHost(dbPath, new ConcurrentBag<ExecutionEvent>(), rack, services =>
        {
            services.AddSingleton<PlcDriverFactory>();
            services.Replace(ServiceDescriptor.Singleton<IPlcDriverFactory>(
                sp => new SwitchableFactory(sp.GetRequiredService<PlcDriverFactory>(), link)));
        });
        try
        {
            var equipmentId = await SeedOrphanAsync(host, rack, "ORPHAN-3");
            host.Services.GetRequiredService<BatchSchedulerHostedService>().PendingResetRetryInterval =
                TimeSpan.FromMilliseconds(150);

            await host.StartAsync();

            // 链路断着：等到至少尝试过一次，标记仍在、设备仍是脏的。
            Assert.True(await WaitUntilAsync(host, Guid.Empty, done => done,
                _ => Task.FromResult(link.Attempts >= 2), timeoutMs: 8_000, intervalMs: 50), "断链期间应当持续重试");
            using (var scope = host.Services.CreateScope())
                Assert.True(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PendingDeviceResets.AnyAsync());
            Assert.True(rack.Get(equipmentId).ReadSignals().StepRunning);

            link.Down = false;

            Assert.True(await WaitUntilAsync(host, Guid.Empty, done => done,
                _ => Task.FromResult(!rack.Get(equipmentId).ReadSignals().StepRunning), timeoutMs: 8_000, intervalMs: 50),
                "链路恢复后必须补做复位");
            Assert.True(await WaitUntilAsync(host, Guid.Empty, done => done,
                async sp => !await sp.GetRequiredService<AppDbContext>().PendingDeviceResets.AnyAsync(),
                timeoutMs: 5_000, intervalMs: 50));
        }
        finally
        {
            DisposeHost(dbPath, host);
        }
    }

    // ------------------------------------------------------------ 工装

    /// <summary>建一台设备，设备上留着残留的 Step_Running，库里留着一条欠账（模拟"中止后调度器没来得及复位"就崩了）。</summary>
    private static async Task<Guid> SeedOrphanAsync(Microsoft.Extensions.Hosting.IHost host, SimulatedPlcRack rack, string code)
    {
        Guid equipmentId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SchemaBootstrap.ApplyAsync(db);
            var equipment = new EquipmentLine(code, "orphan", PlcProtocol.Simulator, "127.0.0.1", 102,
                "S7_1200", 0, 1, SimulatorTagMap, "it");
            db.Equipment.Add(equipment);
            db.PendingDeviceResets.Add(new PendingDeviceReset(equipment.Id, Guid.NewGuid(), "BDEAD", "批次 BDEAD 中止"));
            await db.SaveChangesAsync();
            equipmentId = equipment.Id;
        }

        var station = rack.Get(equipmentId);
        var parameters = new float[16];
        parameters[15] = 120;   // 工艺时长 120s：测试期间不会自己跑完
        station.WritePayload(999, 1, parameters);
        station.SetTrigger(true);
        return equipmentId;
    }

    private static async Task<(BatchService Service, AppDbContext Db, Guid BatchId, Guid EquipmentId)> ArrangeBatchAsync(
        string label, bool started)
    {
        var db = ServiceHarness.OpenDb("brmes-pending-reset");
        var hasher = new BcryptPasswordHasher();
        var operatorUser = new AppUser($"op-{label}", "操作员", hasher.Hash(Password), UserRole.Operator);
        db.Users.Add(operatorUser);
        await db.SaveChangesAsync();

        var user = new ServiceHarness.RoleUser(operatorUser.Id, UserRole.Operator, operatorUser.UserName, "操作员");
        var service = ServiceHarness.NewBatchService(
            db, user, new ServiceHarness.RecordingScheduler(), hasher, new ServiceHarness.NoopPdf(),
            new ServiceHarness.NoopPublisher(), ServiceHarness.NewMaterialLotService(db, user, hasher),
            new EquipmentLeaseService(db, NullLogger<EquipmentLeaseService>.Instance));

        var (batchId, equipmentId) = await SeedTwoStepBatchAsync(db, $"PR-{label}", $"ITPR{label}", $"BPR-{label}");
        if (started)
        {
            var batch = await db.Batches.SingleAsync(b => b.Id == batchId);
            batch.MarkRunning(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        return (service, db, batchId, equipmentId);
    }

    private sealed class LinkSwitch
    {
        public volatile bool Down;
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public void Attempt() => Interlocked.Increment(ref _attempts);
    }

    /// <summary>可以"拔网线"的驱动工厂：断开时创建驱动就失败，等同于 PLC 连不上。</summary>
    private sealed class SwitchableFactory(IPlcDriverFactory inner, LinkSwitch link) : IPlcDriverFactory
    {
        public IPlcHandshakeClient Create(EquipmentLine equipment)
        {
            link.Attempt();
            return link.Down ? throw new IOException("链路断开（测试）") : inner.Create(equipment);
        }
    }
}
