using Microsoft.EntityFrameworkCore;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// signature_records 迁移的**历史行回填**。空库上迁移永远不会错，
/// 真正有风险的是 audit_logs 里已经存在的签名行：拆错一个字，就等于改写了一份已签署的含义。
/// 含义原文在测试里写成字面量，故意不引用 <c>ElectronicSignature.Batch</c>：
/// 迁移冻结的是"当时的措辞"，之后改代码里的措辞不应该让这条测试跟着变。
/// </summary>
public sealed class SignatureRecordsMigrationTests
{
    private const string OldSchema = "20261002120000_SchedulerIntentPerStep";

    private const string StartMeaning = "我作为操作员确认控制配方快照完整有效，启动本批四步握手，禁止盲写。";
    private const string ReleaseMeaning = "我作为质量审核人对照归档质检与四步握手，批准本批放行。";
    private const string LabMeaning = "我作为质量审核人对照规格判定本样品。";
    private const string OlderWording = "我作为操作员确认保持（早期措辞，现行代码里已经没有这句）。";

    [Fact]
    public async Task Migrate_SplitsHistoricalEsignRows_IntoFrozenMeaningAndDetail()
    {
        var path = Path.Combine(Path.GetTempPath(), $"brmes-sigmig-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;

        await using (var old = new AppDbContext(options))
        {
            await old.Database.MigrateAsync(OldSchema, CancellationToken.None);
            await Audit(old, "start", "batch.start.esign", "ProductionBatch", "b1", $"{StartMeaning} BATCH-001");
            await Audit(old, "release", "batch.release.esign", "ProductionBatch", "b1", ReleaseMeaning);
            await Audit(old, "hold", "batch.hold.esign", "ProductionBatch", "b1", $"{OlderWording} 管道堵塞");
            await Audit(old, "lab", "lab.sample.dispose.esign", "LabSample", "s1", $"{LabMeaning} batch=b1 S-1:Pass");
            // 不该被拷走的两类：同对象的非签名审计；别的对象类型的签名（配方签名含义已冻在 approval_records）。
            await Audit(old, "create", "batch.create", "ProductionBatch", "b1", "BATCH-001");
            await Audit(old, "recipe", "recipe.procedure.esign", "MasterRecipe", "r1", "我作为工艺工程师确认 …");
        }

        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync(CancellationToken.None);

            var rows = await db.SignatureRecords.AsNoTracking().ToListAsync();
            Assert.Equal(4, rows.Count);

            var start = rows.Single(r => r.Action == "batch.start.esign");
            Assert.Equal(StartMeaning, start.Meaning);
            Assert.Equal("BATCH-001", start.Detail);
            Assert.Equal("ProductionBatch", start.EntityType);
            Assert.Equal("b1", start.EntityId);
            Assert.Equal("alice", start.SignerName);
            // 沿用审计行的 Id：能反查，也让重复回填天然幂等。
            Assert.Equal(Guid.Parse(IdFor("start")), start.Id);

            var release = rows.Single(r => r.Action == "batch.release.esign");
            Assert.Equal(ReleaseMeaning, release.Meaning);
            Assert.Null(release.Detail);

            // 现行代码里已经没有的措辞：整段原样留作含义，一个字不改，也不编一句他没签过的话。
            var hold = rows.Single(r => r.Action == "batch.hold.esign");
            Assert.Equal($"{OlderWording} 管道堵塞", hold.Meaning);
            Assert.Null(hold.Detail);

            var lab = rows.Single(r => r.Action == "lab.sample.dispose.esign");
            Assert.Equal(LabMeaning, lab.Meaning);
            Assert.Equal("batch=b1 S-1:Pass", lab.Detail);
            Assert.Equal("LabSample", lab.EntityType);

            Assert.DoesNotContain(rows, r => r.Action is "batch.create" or "recipe.procedure.esign");

            // 审计履历原样保留（回填是拷贝，不是搬家）。
            Assert.Equal(6, await db.AuditLogs.CountAsync());

            // 时间列带的是定宽文本映射，能按时间排序读回来。
            var ordered = await db.SignatureRecords.AsNoTracking().OrderBy(r => r.SignedAt).ToListAsync();
            Assert.Equal(4, ordered.Count);
        }
    }

    [Fact]
    public async Task Migrate_OnEmptyDatabase_CreatesTheTable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"brmes-sigmig-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync(CancellationToken.None);
        Assert.Empty(await db.SignatureRecords.ToListAsync());
    }

    private static string IdFor(string key) => key switch
    {
        "start" => "00000000-0000-0000-0000-000000000001",
        "release" => "00000000-0000-0000-0000-000000000002",
        "hold" => "00000000-0000-0000-0000-000000000003",
        "lab" => "00000000-0000-0000-0000-000000000004",
        "create" => "00000000-0000-0000-0000-000000000005",
        "recipe" => "00000000-0000-0000-0000-000000000006",
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private static Task Audit(AppDbContext db, string key, string action, string entityType, string entityId, string detail) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO audit_logs ("Id","UserId","UserName","Action","EntityType","EntityId","Detail","At","CreatedAt")
            VALUES ({0}, NULL, 'alice', {1}, {2}, {3}, {4}, '2026-09-01 10:00:00+00:00', '2026-09-01 10:00:00+00:00');
            """,
            IdFor(key), action, entityType, entityId, detail);
}
