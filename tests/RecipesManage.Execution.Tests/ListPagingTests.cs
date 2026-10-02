using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Materials;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 列表的服务端分页 / 筛选 / 排序，以及趋势样本的抽稀口径。
///
/// 这一批改的动因：以前接口只回"最近 N 条"，前端既看不到更早的批次，排序也只在这 N 条里排，
/// 于是"按状态排序"给出的是一个看着完整其实错的结论。既然排序与筛选都进了 SQL，
/// 就要在 SQL 这一侧钉住：翻页不重不漏、通配符被转义、次序用的是业务生命周期而不是枚举底序。
/// </summary>
public sealed class ListPagingTests
{
    private static readonly Guid OperatorId = Guid.NewGuid();

    /// <summary>本类只测读路径（列表 / 趋势 / 报警 / 握手履历），所以直接用查询服务。</summary>
    private static BatchQueryService Batches(AppDbContext db)
    {
        var user = new ServiceHarness.RoleUser(OperatorId, UserRole.Operator, "operator", "车间操作员");
        return ServiceHarness.NewBatchQuery(db, user, Lots(db));
    }

    private static MaterialLotService Lots(AppDbContext db) =>
        new MaterialLotService(db, new ServiceHarness.RoleUser(OperatorId, UserRole.Operator), new BcryptPasswordHasher());

    /// <summary>建一批批次，按给定顺序逐个推向目标状态。</summary>
    private static async Task<List<ProductionBatch>> SeedBatches(
        AppDbContext db, Guid? equipmentId = null, params BatchStatus[] statuses)
    {
        var made = new List<ProductionBatch>();
        var index = 0;
        foreach (var status in statuses)
        {
            var batch = ServiceHarness.Batch($"BLP{index++:D2}", equipmentId);
            Advance(batch, status);
            db.Batches.Add(batch);
            made.Add(batch);
        }
        await db.SaveChangesAsync();
        return made;
    }

    /// <summary>沿真实状态机走过去，不开后门改 Status——绕过领域方法，测试就在验证一个生产到不了的组合。</summary>
    private static void Advance(ProductionBatch batch, BatchStatus target)
    {
        var now = DateTimeOffset.UtcNow;
        if (target == BatchStatus.Created) return;
        batch.Queue();
        if (target == BatchStatus.Queued) return;
        batch.MarkRunning(now);
        switch (target)
        {
            case BatchStatus.Running:
                return;
            case BatchStatus.Held:
                batch.Hold("保持测试");
                return;
            case BatchStatus.Faulted:
                batch.Fault("E2E", "握手故障");
                return;
            case BatchStatus.Aborted:
                batch.Abort("中止测试");
                return;
        }

        if (batch.Status == BatchStatus.Faulted) batch.Queue();
        batch.MarkRunning(now);
        batch.Complete(now);
        if (target == BatchStatus.Completed) return;
        if (target == BatchStatus.DispositionRejected)
            batch.RejectDisposition("qa", "对照质检拒收", now);
        else
            batch.Release("qa", "质量放行", now);
    }

    [Fact]
    public async Task StatusSortUsesTheLifecycleOrderNotTheEnumOrdinal()
    {
        await using var db = ServiceHarness.OpenDb();
        await SeedBatches(db, null, BatchStatus.Released, BatchStatus.Held, BatchStatus.Completed);

        var ascending = (await Batches(db).ListAsync(0, 50, "status", "asc", null, null, false, CancellationToken.None))
            .Items.Select(b => b.Status).ToList();

        // 枚举底序会把 Completed(3) 排在 Held(6) 前面；生命周期次序要求先看在跑的、再看待放行的。
        Assert.Equal(
            [BatchStatus.Held, BatchStatus.Completed, BatchStatus.Released],
            ascending);

        var descending = (await Batches(db).ListAsync(0, 50, "status", "desc", null, null, false, CancellationToken.None))
            .Items.Select(b => b.Status).ToList();
        Assert.Equal(ascending.AsEnumerable().Reverse().ToList(), descending);
    }

    [Fact]
    public async Task PagingCoversEveryRowExactlyOnce_AndTotalCountsTheWholeFilter()
    {
        await using var db = ServiceHarness.OpenDb();
        var seeded = await SeedBatches(db, null, Enumerable.Repeat(BatchStatus.Created, 7).ToArray());
        Assert.Equal(7, seeded.Count);
        var batches = Batches(db);

        var seen = new List<Guid>();
        for (var page = 0; page < 4; page++)
        {
            var result = await batches.ListAsync(page * 2, 2, "batchNo", "asc", null, null, false, CancellationToken.None);
            Assert.Equal(7, result.Total);      // Total 是筛选后的全量，不是本页行数
            seen.AddRange(result.Items.Select(b => b.Id));
        }

        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    public async Task SearchIsLiteral_AndWildcardsInTheQueryDoNotMatchEverything()
    {
        await using var db = ServiceHarness.OpenDb();
        var rack = new EquipmentLine("HT-E2E", "共振加热槽", PlcProtocol.Simulator, "127.0.0.1", 102, "S7_1200", 0, 1, "{}", "it");
        db.Equipment.Add(rack);
        await SeedBatches(db, rack.Id, BatchStatus.Created, BatchStatus.Created, BatchStatus.Released);
        var batches = Batches(db);

        Assert.Equal(3, (await batches.ListAsync(0, 50, null, null, "BLP", null, false, CancellationToken.None)).Total);
        Assert.Single((await batches.ListAsync(0, 50, null, null, "BLP01", null, false, CancellationToken.None)).Items);
        // 用户输入 % 或 _ 当字面量看：不转义的话 "%" 等于"匹配所有"，搜索结果会谎报覆盖面。
        Assert.Equal(0, (await batches.ListAsync(0, 50, null, null, "%", null, false, CancellationToken.None)).Total);
        Assert.Equal(0, (await batches.ListAsync(0, 50, null, null, "BLP0_", null, false, CancellationToken.None)).Total);
        // 联表列也在搜索范围内（设备码在批次行上只有 Id，界面显示的码来自这次连接）。
        Assert.Equal(3, (await batches.ListAsync(0, 50, null, null, "HT-E2E", null, false, CancellationToken.None)).Total);
        Assert.Equal(3, (await batches.ListAsync(0, 50, null, null, "part", null, false, CancellationToken.None)).Total);
        // 配方没有对应行时（快照里冻结的名字没有联表可搜），批次仍然要列得出来，只是搜不到。
        Assert.Equal(3, (await batches.ListAsync(0, 50, null, null, null, null, false, CancellationToken.None)).Total);
    }

    [Fact]
    public async Task StatusFilterParsesTheEnumButIgnoresGarbage()
    {
        await using var db = ServiceHarness.OpenDb();
        await SeedBatches(db, null, BatchStatus.Created, BatchStatus.Released);
        var batches = Batches(db);

        Assert.Single((await batches.ListAsync(0, 50, null, null, null, "Released", false, CancellationToken.None)).Items);
        // 筛选串由前端拼：脏值该忽略（回全量），不该让列表 500。
        Assert.Equal(2, (await batches.ListAsync(0, 50, null, null, null, "not-a-status", false, CancellationToken.None)).Total);
    }

    [Fact]
    public async Task OnlyLabPendingKeepsExactlyTheBatchesAwaitingAFinalDisposition()
    {
        await using var db = ServiceHarness.OpenDb();
        var seeded = await SeedBatches(db, null, BatchStatus.Completed, BatchStatus.Completed, BatchStatus.Created);
        var now = DateTimeOffset.UtcNow;
        db.LabSamples.Add(new LabSample("FIN-1", seeded[0].Id, LabSampleType.Final, "qa", now));
        db.LabSamples.Add(new LabSample("IN-1", seeded[1].Id, LabSampleType.InProcess, "qa", now));
        var passed = new LabSample("FIN-2", seeded[1].Id, LabSampleType.Final, "qa", now);
        passed.RecordDisposition(LabSampleDisposition.Pass, "qa", "合格", now);
        db.LabSamples.Add(passed);
        await db.SaveChangesAsync();
        var batches = Batches(db);

        var pending = await batches.ListAsync(0, 50, null, null, null, null, true, CancellationToken.None);
        // 只有"终样待判"的那一批算待检：过程样不算，已判合格的也不算。
        Assert.Equal([seeded[0].Id], pending.Items.Select(b => b.Id));
        Assert.True(pending.Items.Single().PendingFinalSample);
        Assert.Equal(3, (await batches.ListAsync(0, 50, null, null, null, null, false, CancellationToken.None)).Total);
    }

    [Fact]
    public async Task TrendSamplesAreStridedAcrossTheWholeBatch()
    {
        await using var db = ServiceHarness.OpenDb();
        var batch = ServiceHarness.Batch("BLPSAMP");
        db.Batches.Add(batch);
        await db.SaveChangesAsync();

        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 600; i++)
        {
            db.ProcessSamples.Add(new ProcessSample(batch.Id, null, start.AddSeconds(i), "Temperature", 500 + i % 7, "℃"));
            db.ProcessSamples.Add(new ProcessSample(batch.Id, null, start.AddSeconds(i), "Pressure", 1 + i % 3, "bar"));
        }
        await db.SaveChangesAsync();
        var batches = Batches(db);

        var full = await batches.SamplesAsync(batch.Id, 5_000, CancellationToken.None);
        Assert.Equal(1_200, full.Total);
        Assert.Equal(1, full.Step);                       // 上限内就不取样
        Assert.Equal(1_200, full.Points.Count);

        var thin = await batches.SamplesAsync(batch.Id, 50, CancellationToken.None);
        Assert.Equal(1_200, thin.Total);                  // 原始行数一个不少，只是没全发给浏览器
        Assert.Equal(12, thin.Step);                      // ceil(600 / 50)
        Assert.True(thin.Points.Count <= 50 * 2, $"每个测点各不超过上限：实际 {thin.Points.Count}");

        // 关键：取样必须覆盖整批。旧实现是"读最近 5 万行再抽稀"，长跑批次的开头会被整段丢掉。
        Assert.Equal(start, thin.Points.Min(p => p.SampledAt));
        Assert.InRange(thin.Points.Max(p => p.SampledAt), start.AddSeconds(600 - thin.Step), start.AddSeconds(599));
        // 抽稀后仍要按时间正序，画布拿到乱序会画出回折的线。
        Assert.Equal(thin.Points.OrderBy(p => p.SampledAt).ToList(), thin.Points);
        Assert.All(thin.Points.GroupBy(p => p.Tag), g => Assert.True(g.Count() <= 50, $"{g.Key} 取样后仍有 {g.Count()} 点"));

        var empty = await batches.SamplesAsync(Guid.NewGuid(), 50, CancellationToken.None);
        Assert.Equal(0, empty.Total);
        Assert.Empty(empty.Points);
    }

    [Fact]
    public async Task AlarmListPagesAndFiltersToUnacknowledged()
    {
        await using var db = ServiceHarness.OpenDb();
        var batch = ServiceHarness.Batch("BLPALM");
        db.Batches.Add(batch);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            var alarm = new ProcessAlarm(batch.Id, batch.BatchNo, null, "S10", $"ALM{i}", "Fault", $"报警 {i}", now.AddMinutes(i));
            if (i % 2 == 0) alarm.Acknowledge("operator", now.AddMinutes(i + 1));
            db.ProcessAlarms.Add(alarm);
        }
        await db.SaveChangesAsync();
        var batches = Batches(db);

        var all = await batches.AlarmsAsync(batch.Id, 0, 50, null, null, null, false, CancellationToken.None);
        Assert.Equal(5, all.Total);
        // 默认按时间倒序：最新的报警先看，它往往是还没处理的那一个。
        Assert.Equal("ALM4", all.Items.First().Code);

        var open = await batches.AlarmsAsync(batch.Id, 0, 50, null, null, null, true, CancellationToken.None);
        Assert.Equal(2, open.Total);
        Assert.All(open.Items, a => Assert.Null(a.AcknowledgedAt));

        Assert.Equal(1, (await batches.AlarmsAsync(batch.Id, 0, 50, null, null, "ALM3", false, CancellationToken.None)).Total);
        // 通配符按字面量处理：报警的码与说明里没有 %，所以 "%" 应该什么都搜不到，而不是搜到全部。
        Assert.Equal(0, (await batches.AlarmsAsync(batch.Id, 0, 50, null, null, "%", false, CancellationToken.None)).Total);
        Assert.Equal(2, (await batches.AlarmsAsync(batch.Id, 0, 2, "raisedAt", "asc", null, true, CancellationToken.None)).Items.Count);
    }

    [Fact]
    public async Task LotStatusFilterAcceptsSeveralValuesAndIgnoresGarbage()
    {
        await using var db = ServiceHarness.OpenDb();
        var open = MaterialLot.Receive("LOT-OPEN", "AL6061", "铝合金锭", 10, "kg");
        var released = MaterialLot.Receive("LOT-REL", "AL6061", "铝合金锭", 10, "kg");
        released.MarkReleased();
        var consumed = MaterialLot.Receive("LOT-CON", "AL6061", "铝合金锭", 10, "kg");
        consumed.MarkConsumed();
        db.MaterialLots.AddRange(open, released, consumed);
        await db.SaveChangesAsync();
        var lots = Lots(db);

        Assert.Equal(2, (await lots.ListAsync(0, 50, null, null, null, "Open,Released", CancellationToken.None)).Total);
        Assert.Equal(3, (await lots.ListAsync(0, 50, null, null, null, null, CancellationToken.None)).Total);
        Assert.Equal(3, (await lots.ListAsync(0, 50, null, null, null, "bogus", CancellationToken.None)).Total);
        // 状态列按业务次序排（Open → Quarantine → Released → Consumed），枚举底序会把 Consumed 提前。
        var ordered = await lots.ListAsync(0, 50, "status", "asc", null, null, CancellationToken.None);
        Assert.Equal(["LOT-OPEN", "LOT-REL", "LOT-CON"], ordered.Items.Select(l => l.LotNumber));
    }

    [Fact]
    public async Task TrendStrideSqlAlsoRunsAgainstAMigratedSchema()
    {
        // 取样那条 SQL 把列名写死了，所以必须在"迁移建出来的库"上跑一次：
        // 其余测试走 EnsureCreated，列名漂移（改属性名但忘了改迁移）只有这条会红。
        var path = Path.Combine(Path.GetTempPath(), $"brmes-stride-{Guid.NewGuid():N}.db");
        try
        {
            var builder = new DbContextOptionsBuilder<AppDbContext>();
            RecipesDatabase.Apply(builder, $"Data Source={path}");
            await using var db = new AppDbContext(builder.Options);
            await SchemaBootstrap.ApplyAsync(db);

            var batch = ServiceHarness.Batch("BSTRIDE");
            db.Batches.Add(batch);
            await db.SaveChangesAsync();
            var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 120; i++)
                db.ProcessSamples.Add(new ProcessSample(batch.Id, null, start.AddSeconds(i), "Temperature", 500 + i, "℃"));
            await db.SaveChangesAsync();

            var series = await Batches(db).SamplesAsync(batch.Id, 50, CancellationToken.None);

            Assert.Equal(120, series.Total);
            Assert.Equal(3, series.Step);                     // ceil(120 / 50)
            Assert.Equal(40, series.Points.Count);            // rn = 1,4,7,…,118
            Assert.Equal(start, series.Points.First().SampledAt);          // 整批开头在
            Assert.Equal(start.AddSeconds(117), series.Points.Last().SampledAt);   // 末尾只差一步长，没被截掉
        }
        finally
        {
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); } catch (IOException) { /* 临时文件 */ }
        }
    }

    [Fact]
    public async Task HandshakeLogIsWindowedAndSaysSo()
    {
        await using var db = ServiceHarness.OpenDb();
        var batch = ServiceHarness.Batch("BLHLOG");
        db.Batches.Add(batch);
        await db.SaveChangesAsync();
        for (var i = 0; i < 25; i++)
        {
            db.HandshakeEvents.Add(new HandshakeEvent(
                batch.Id, batch.CurrentStepId, "S10", $"Phase{i:D2}", "phase", null, null));
            await db.SaveChangesAsync();   // 逐条落库：CreatedAt 在构造时取，同一批里要能分出先后
        }

        var page = await Batches(db).HandshakeLogAsync(batch.Id, 10, CancellationToken.None);

        Assert.Equal(25, page.Total);                     // 全量条数要报出来，否则用户以为履历就 10 条
        Assert.Equal(10, page.Items.Count);
        Assert.Contains("Phase24", page.Items.Select(i => i.Phase));   // 取的是最近 10 条，不是最老的
        Assert.Equal(page.Items.Select(i => i.At).OrderBy(x => x), page.Items.Select(i => i.At));
    }
}
