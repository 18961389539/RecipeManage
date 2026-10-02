using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 跳步安全门只认一个真源：<c>batch_lanes</c> 行的相位。
///
/// 以前它按"车道行 → 最后一条握手事件 → 解析批次展示串 → 直接拿展示串"四级兜底来定相位，
/// 于是"要不要禁止盲写 PLC"这个安全判定会被告警条上那串展示文本影响。
/// 现在读侧收口：有行看行，没行按不可跳处理（引擎在 MarkRunning 之前就给每条绑定设备建好了行，
/// 没行只可能是数据被外力破坏）。这几条测试就是钉住这个 fail-closed 方向。
/// </summary>
public sealed class SkipPhaseGateTests
{
    private const string Password = "Supervisor@123";

    [Fact]
    public async Task Skip_Allowed_WhenLaneRowSaysPlcReady_EvenIfDisplayClaimsRunning()
    {
        var (service, db, scheduler, batchId, stepId) = await ArrangeAsync(
            nameof(Skip_Allowed_WhenLaneRowSaysPlcReady_EvenIfDisplayClaimsRunning),
            lanePhase: nameof(HandshakePhase.WaitingPlcReady),
            display: "HT-GATE:StepRunning");

        await service.SkipAsync(batchId, "就绪时跳步", Password, stepId, CancellationToken.None);

        Assert.Single(scheduler.Skips, s => s.BatchId == batchId && s.StepId == stepId);
        Assert.NotNull(await db.SchedulerIntents.AsNoTracking()
            .SingleOrDefaultAsync(i => i.BatchId == batchId && i.Kind == SchedulerIntentKinds.Skip));
    }

    [Fact]
    public async Task Skip_Refused_WhenLaneRowSaysStepRunning_EvenIfDisplayClaimsReady()
    {
        var (service, db, scheduler, batchId, stepId) = await ArrangeAsync(
            nameof(Skip_Refused_WhenLaneRowSaysStepRunning_EvenIfDisplayClaimsReady),
            lanePhase: nameof(HandshakePhase.StepRunning),
            display: "WaitingPlcReady");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SkipAsync(batchId, "运行中想跳步", Password, stepId, CancellationToken.None));

        Assert.Equal("SKIP_UNSAFE", ex.Code);
        Assert.Contains(nameof(HandshakePhase.StepRunning), ex.Message, StringComparison.Ordinal);
        Assert.Empty(scheduler.Skips);
        Assert.Empty(await db.SchedulerIntents.AsNoTracking()
            .Where(i => i.BatchId == batchId && i.Kind == SchedulerIntentKinds.Skip)
            .ToListAsync());
    }

    [Fact]
    public async Task Skip_Refused_WhenLaneRowMissing_FailsClosedInsteadOfGuessingFromDisplay()
    {
        var (service, _, scheduler, batchId, stepId) = await ArrangeAsync(
            nameof(Skip_Refused_WhenLaneRowMissing_FailsClosedInsteadOfGuessingFromDisplay),
            lanePhase: null,
            display: "WaitingPlcReady");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SkipAsync(batchId, "没有车道行也想跳步", Password, stepId, CancellationToken.None));

        Assert.Equal("SKIP_UNSAFE", ex.Code);
        Assert.Empty(scheduler.Skips);
    }

    /// <summary>暂停态跳步不查车道相位（引擎那时已让出 PLC），但履历里必须写真实相位而不是展示串。</summary>
    [Fact]
    public async Task Skip_WhileHeld_RecordsLanePhase_NotTheMergedDisplay()
    {
        var (service, db, _, batchId, stepId) = await ArrangeAsync(
            nameof(Skip_WhileHeld_RecordsLanePhase_NotTheMergedDisplay),
            lanePhase: HandshakeView.Held,
            display: "HT-GATE:Held · HT-OTHER:StepRunning",
            held: true);

        await service.SkipAsync(batchId, "暂停中跳步", Password, stepId, CancellationToken.None);

        var skip = await db.HandshakeEvents.AsNoTracking()
            .SingleAsync(e => e.BatchId == batchId && e.StepId == stepId && e.Kind == "skip");
        Assert.Equal(HandshakeView.Held, skip.Phase);
    }

    /// <summary>建一个 Running（或 Held）批次：车道行相位与被试工步由参数决定，批次展示串故意可以"撒谎"。</summary>
    private static async Task<(BatchService Service, AppDbContext Db, ServiceHarness.RecordingScheduler Scheduler, Guid BatchId, Guid StepId)>
        ArrangeAsync(string label, string? lanePhase, string display, bool held = false)
    {
        var db = ServiceHarness.OpenDb("brmes-skipgate");
        var hasher = new BcryptPasswordHasher();
        var supervisor = new AppUser($"sup{Math.Abs(label.GetHashCode() % 9999):D4}", "工艺主管",
            hasher.Hash(Password), UserRole.Supervisor);
        db.Users.Add(supervisor);
        await db.SaveChangesAsync();

        var user = new ServiceHarness.RoleUser(supervisor.Id, UserRole.Supervisor, supervisor.UserName, "工艺主管");
        var scheduler = new ServiceHarness.RecordingScheduler();
        var service = ServiceHarness.NewBatchService(
            db, user, scheduler, hasher, new ServiceHarness.NoopPdf(), new ServiceHarness.NoopPublisher(),
            ServiceHarness.NewMaterialLotService(db, user, hasher),
            new EquipmentLeaseService(db, NullLogger<EquipmentLeaseService>.Instance));

        const string equipmentCode = "HT-GATE";
        var suffix = Math.Abs(label.GetHashCode() % 9999).ToString("D4");
        var (batchId, equipmentId) = await SchedulerHarness.SeedApprovedBatchAsync(
            db, equipmentCode, $"ITG{suffix}", $"BGT{suffix}",
            draftId =>
            [
                SchedulerHarness.Step(draftId, "S10", "heat", StepType.Heat, 0,
                    new SchedulerHarness.Param("目标温度", "℃", 120, 100, 200)),
                SchedulerHarness.Step(draftId, "S20", "hold", StepType.Hold, 1,
                    new SchedulerHarness.Param("保温时长", "s", 1, 0.5, 5))
            ]);

        var batch = await db.Batches.Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
        var snapshot = SnapshotJson.Deserialize(batch.ControlRecipeJson)!;
        var stepId = snapshot.Steps[0].StepId;

        batch.MarkRunning(DateTimeOffset.UtcNow);
        batch.StepExecutions.Single(e => e.StepId == stepId).MarkStarted(DateTimeOffset.UtcNow);
        batch.UpdateHandshake(display);
        if (lanePhase is not null)
        {
            var lane = new BatchLane(batchId, equipmentId, equipmentCode, "UP-01 热处理单元");
            lane.Update(lanePhase, StepOutcome.Running, stepId, "S10");
            db.Lanes.Add(lane);
        }
        await db.SaveChangesAsync();

        if (held)
        {
            batch.Hold("测试保持");
            await db.SaveChangesAsync();
        }

        return (service, db, scheduler, batchId, stepId);
    }
}
