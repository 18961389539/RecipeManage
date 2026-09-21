using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Recipes;
using RecipesManage.Execution;
using RecipesManage.Infrastructure.Persistence;
using RecipesManage.Infrastructure.Plc;
using Xunit;

namespace RecipesManage.Execution.Tests;

[Collection("EmbeddedPostgres")]
public sealed class PostgresHandshakeTests
{
    private readonly EmbeddedPostgresFixture _pg;

    public PostgresHandshakeTests(EmbeddedPostgresFixture pg) => _pg = pg;

    [Fact(Timeout = 300_000)]
    public async Task Scheduler_OnPostgreSqlJsonb_CompletesFourStepHandshake()
    {
        var cs = await _pg.Runtime.EnsureDatabaseAsync("brmes_exec");
        Guid batchId;
        var recipeCode = "";
        var events = new ConcurrentBag<ExecutionEvent>();
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddDbContext<AppDbContext>(o => RecipesDatabase.Apply(o, cs, null));
                services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
                services.AddSingleton<SimulatedPlcRack>();
                services.AddSingleton<IPlcDriverFactory, PlcDriverFactory>();
                services.AddSingleton<IExecutionPublisher>(new CapturingPublisher(events));
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
                await SchemaBootstrap.ApplyAsync(db);
                Assert.Equal("jsonb", db.Model.FindEntityType(typeof(ProductionBatch))!
                    .FindProperty(nameof(ProductionBatch.ControlRecipeJson))!.GetColumnType());

                var equipment = new EquipmentLine(
                    "HTPG" + Guid.NewGuid().ToString("N")[..6], "pg furnace", PlcProtocol.Simulator, "127.0.0.1", 102,
                    "S7_1200", 0, 1, "{}", "it");
                db.Equipment.Add(equipment);

                var recipe = MasterRecipe.Create("ITPG" + Guid.NewGuid().ToString("N")[..6], "postgres handshake", "P", "part", null, Guid.NewGuid());
                recipeCode = recipe.Code;
                var draft = recipe.RequireDraft();
                var s1 = new RecipeStep(draft.Id, "S10", "heat", StepType.Heat, 0, 0, 0, 30, null,
                    [new RecipeParameter(0, "目标温度", "℃", 120, 100, 200, true, true),
                     new RecipeParameter(1, "时长", "s", 1, 0.5, 5, true, true)]);
                var s2 = new RecipeStep(draft.Id, "S20", "hold", StepType.Hold, 1, 100, 0, 30, null,
                    [new RecipeParameter(0, "保温温度", "℃", 120, 100, 200, true, true),
                     new RecipeParameter(1, "保温时长", "s", 1, 0.5, 5, true, true)]);
                draft.ReplaceProcedure([s1, s2], [new RecipeEdge(draft.Id, s1.Id, s2.Id)]);
                draft.Submit(DateTimeOffset.UtcNow);
                draft.Decide(ApprovalLevel.Supervisor, Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                draft.Decide(ApprovalLevel.Quality, Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
                recipe.MarkApproved(draft);
                db.Recipes.Add(recipe);
                await db.SaveChangesAsync();

                var snapshot = ControlRecipeSnapshotFactory.From(recipe, draft, DateTimeOffset.UtcNow);
                SnapshotIntegrity.Seal(snapshot, BatchService.JsonOptions, out var json);
                var batchNo = "BITPG" + Guid.NewGuid().ToString("N")[..8];
                var batch = ProductionBatch.Create(batchNo, equipment.Id, snapshot, json, Guid.NewGuid());
                foreach (var step in snapshot.Steps)
                    batch.StepExecutions.Add(new BatchStepExecution(batch.Id, step.StepId, step.Code, step.Name, step.Type, step.Ordinal));
                batch.Queue();
                db.Batches.Add(batch);
                await db.SaveChangesAsync();
                batchId = batch.Id;
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
            Assert.All(live.StepExecutions, e => Assert.Equal("Completed", e.Outcome));

            using (var logScope = host.Services.CreateScope())
            {
                var db = logScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
                Assert.Contains(log, e => e.Kind == "write");
                Assert.Contains(log, e => e.Kind == "trigger");
                Assert.Contains(log, e => e.Kind == "archive");
                Assert.Contains(log, e => e.Kind == "advance");

                Assert.Contains(recipeCode, live.ControlRecipeJson, StringComparison.Ordinal);

                await db.Database.OpenConnectionAsync();
                await using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = """
                    SELECT "ControlRecipeJson" @> ('{"recipeCode":"' || @code || '"}')::jsonb
                    FROM "production_batches"
                    WHERE "Id" = @id
                    """;
                var idParam = cmd.CreateParameter();
                idParam.ParameterName = "id";
                idParam.Value = batchId;
                cmd.Parameters.Add(idParam);
                var codeParam = cmd.CreateParameter();
                codeParam.ParameterName = "code";
                codeParam.Value = recipeCode;
                cmd.Parameters.Add(codeParam);
                Assert.True(Equals(true, await cmd.ExecuteScalarAsync()),
                    "ControlRecipeJson jsonb 未包含 recipeCode=" + recipeCode);
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
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
