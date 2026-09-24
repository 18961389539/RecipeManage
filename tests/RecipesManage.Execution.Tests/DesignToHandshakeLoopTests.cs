using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using RecipesManage.Execution;
using RecipesManage.Infrastructure.Persistence;
using RecipesManage.Infrastructure.Plc;
using RecipesManage.Infrastructure.Records;
using Xunit;

using static RecipesManage.Execution.Tests.ServiceHarness;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 产品闭环：设计态版本化编排 + 多级审核 → 控制配方快照 → 仿真 PLC 四步握手 → 质检归档。
/// </summary>
public sealed class DesignToHandshakeLoopTests
{
    [Fact(Timeout = 60_000)]
    public async Task AuthorSubmitApproveSnapshot_CompletesFourStepHandshakeAndQualityArchive()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-loop-{Guid.NewGuid():N}.db");
        var events = new ConcurrentBag<ExecutionEvent>();
        var publisher = new CapturingPublisher(events);
        var hasher = new BcryptPasswordHasher();
        Guid batchId;
        string batchNo;
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
                services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
                services.AddSingleton<SimulatedPlcRack>();
                services.AddSingleton<IPlcDriverFactory, PlcDriverFactory>();
                services.AddSingleton<IExecutionPublisher>(publisher);
                services.AddSingleton<BatchSchedulerHostedService>();
                services.AddSingleton<IBatchScheduler>(sp => sp.GetRequiredService<BatchSchedulerHostedService>());
                services.AddHostedService(sp => sp.GetRequiredService<BatchSchedulerHostedService>());
            })
            .Build();

        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();

                var engineer = new AppUser("engineer", "工艺工程师", hasher.Hash("Engineer@123"), UserRole.ProcessEngineer);
                var supervisor = new AppUser("supervisor", "工艺主管", hasher.Hash("Supervisor@123"), UserRole.Supervisor);
                var qa = new AppUser("qa", "质量工程师", hasher.Hash("Quality@123"), UserRole.Quality);
                var op = new AppUser("operator", "车间操作员", hasher.Hash("Operator@123"), UserRole.Operator);
                db.Users.AddRange(engineer, supervisor, qa, op);
                var equipment = new EquipmentLine(
                    "HT-LOOP", "loop furnace", PlcProtocol.Simulator, "127.0.0.1", 102,
                    "S7_1200", 0, 1, "{}", "it");
                db.Equipment.Add(equipment);
                await db.SaveChangesAsync();

                var recipes = new RecipeService(db, new RoleUser(engineer.Id, UserRole.ProcessEngineer, "engineer", "工艺工程师"), hasher);
                var header = await recipes.CreateAsync(new CreateRecipeRequest(
                    "LOOP-HT", "闭环热处理", "AL6061", "锻件", "设计到握手"), CancellationToken.None);
                header = await recipes.UpdateHeaderAsync(header.Id, new UpdateRecipeRequest(
                    "闭环热处理 v1", "AL6061", "铝合金锻件", "版本化编排"), CancellationToken.None);
                Assert.Equal("闭环热处理 v1", header.Name);

                var s10 = Guid.NewGuid();
                var s20 = Guid.NewGuid();
                await recipes.SaveProcedureAsync(header.Id, new SaveProcedureRequest(
                    [
                        new SaveStepRequest(s10, "S10", "升温", StepType.Heat, 0, 0, 0, 30, "写 PLC",
                            "UP-01 热处理单元", "OP-Heat 升温",
                            [
                                new SaveParameterRequest(0, "目标温度", "℃", 120, 100, 200, true, true),
                                new SaveParameterRequest(1, "时长", "s", 1, 0.5, 5, true, true)
                            ]),
                        new SaveStepRequest(s20, "S20", "质检采样", StepType.QualityCheck, 1, 160, 0, 30, "禁止写 PLC",
                            "UP-QC 质检", "OP-QC 质检",
                            [new SaveParameterRequest(0, "硬度", "HB", 95, 90, 110, false, true)])
                    ],
                    [new SaveEdgeRequest(s10, s20)],
                    "Engineer@123",
                    "初版 Procedure / Setpoints"), CancellationToken.None);

                var submitted = await recipes.SubmitAsync(header.Id, new SubmitRecipeRequest("Engineer@123", "提交多级审核"), CancellationToken.None);
                Assert.Equal(RecipeStatus.InReview, submitted.Draft!.Status);

                var afterSupervisor = await new RecipeService(db, new RoleUser(supervisor.Id, UserRole.Supervisor, "supervisor", "工艺主管"), hasher)
                    .DecideAsync(header.Id, new DecideRequest(ApprovalDecision.Approved, "路径可执行", "Supervisor@123"), CancellationToken.None);
                Assert.Contains(afterSupervisor.Draft!.Approvals, a => a.Node == ApprovalNode.Quality && a.Decision == ApprovalDecision.Pending);

                var approved = await new RecipeService(db, new RoleUser(qa.Id, UserRole.Quality, "qa", "质量工程师"), hasher)
                    .DecideAsync(header.Id, new DecideRequest(ApprovalDecision.Approved, "窗口合格", "Quality@123"), CancellationToken.None);
                Assert.NotNull(approved.Approved);
                Assert.Equal(1, approved.Approved!.VersionNumber);
                Assert.Equal(RecipeStatus.Approved, approved.Approved.Status);

                var scheduler = host.Services.GetRequiredService<IBatchScheduler>();
                var opUser = new RoleUser(op.Id, UserRole.Operator, "operator", "车间操作员");
                var batches = new BatchService(
                    db,
                    opUser,
                    scheduler,
                    hasher,
                    new BatchRecordPdf(),
                    publisher,
                    new MaterialLotService(db, opUser, hasher),
                    new EquipmentLeaseService(db, NullLogger<EquipmentLeaseService>.Instance));
                var created = await batches.CreateAsync(new CreateBatchRequest(
                    "BLOOP1", header.Id, equipment.Id, 1, "LOT-LOOP"), CancellationToken.None);
                Assert.Equal("Valid", created.SnapshotIntegrity);
                Assert.Equal("LOOP-HT", created.Snapshot.RecipeCode);
                Assert.Equal(2, created.Snapshot.Steps.Count);
                Assert.DoesNotContain(events, e => e.Type == "occupancy");

                await batches.StartAsync(created.Id, "Operator@123", CancellationToken.None);
                Assert.Contains(events, e => e.Type == "occupancy");
                batchId = created.Id;
                batchNo = created.BatchNo;
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
            var logDb = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var log = await logDb.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
            Assert.Contains(log, e => e.StepCode == "S10" && e.Kind == "write" && e.Detail != null && e.Detail.Contains("Step_ID=10"));
            Assert.Contains(log, e => e.Kind == "trigger" && e.Detail != null && e.Detail.Contains("Trigger_Write=1"));
            Assert.Contains(log, e => e.StepCode == "S10" && e.Kind == "archive");
            Assert.Contains(log, e => e.StepCode == "S20" && e.Kind == "quality");
            Assert.DoesNotContain(log, e => e.StepCode == "S20" && e.Kind == "write");

            var heat = live.StepExecutions.Single(e => e.StepCode == "S10");
            Assert.False(string.IsNullOrWhiteSpace(heat.QualityJson));
            Assert.Contains("目标温度", heat.QualityJson, StringComparison.Ordinal);

            var qc = live.StepExecutions.Single(e => e.StepCode == "S20");
            Assert.False(string.IsNullOrWhiteSpace(qc.QualityJson));

            var snapshot = BatchService.Deserialize(live.ControlRecipeJson);
            Assert.Equal("Valid", SnapshotIntegrity.Verify(snapshot!, BatchService.JsonOptions));
            Assert.Equal("LOT-LOOP", snapshot!.LotNumber);

            var operatorUser = await logDb.Users.SingleAsync(u => u.UserName == "operator");
            var recordUser = new RoleUser(operatorUser.Id, UserRole.Operator, "operator", "车间操作员");
            var records = new BatchService(
                logDb,
                recordUser,
                host.Services.GetRequiredService<IBatchScheduler>(),
                hasher,
                new BatchRecordPdf(),
                publisher,
                new MaterialLotService(logDb, recordUser, hasher),
                new EquipmentLeaseService(logDb, NullLogger<EquipmentLeaseService>.Instance));
            var pdf = await records.ExportPdfAsync(batchId, CancellationToken.None);
            var ascii = Encoding.ASCII.GetString(pdf);
            Assert.Contains("pdfaid", ascii, StringComparison.OrdinalIgnoreCase);

            var remaining = await logDb.Batches.AsNoTracking()
                .Where(b => b.Status == BatchStatus.Queued || b.Status == BatchStatus.Running || b.Status == BatchStatus.Held)
                .ToListAsync();
            var equipmentRows = await logDb.Equipment.AsNoTracking().ToListAsync();
            var occupancy = OccupancyRealtime.Snapshot(equipmentRows, remaining);
            var furnace = Assert.Single(occupancy, r => r.Code == "HT-LOOP");
            Assert.Equal("Idle", furnace.Occupancy);
            Assert.Equal(batchNo, live.BatchNo);
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }

    private sealed class CapturingPublisher(ConcurrentBag<ExecutionEvent> sink) : IExecutionPublisher
    {
        public Task PublishAsync(ExecutionEvent evt, CancellationToken cancellationToken = default)
        {
            sink.Add(evt);
            return Task.CompletedTask;
        }
    }
}
