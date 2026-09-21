using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 批次并发写保护。HTTP 请求路径与后台调度线程各自持有独立的 DbContext 副本，
/// 过去双方的写会互相静默覆盖（例如操作员中止后被下一个 tick 覆盖回 Held）。
/// 生产批次现在带 ConcurrencyStamp，后写者必须显式失败。
/// </summary>
public sealed class BatchConcurrencyTests
{
    [Fact]
    public async Task SecondWriterOnStaleBatchCopy_FailsInsteadOfOverwriting()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"brmes-conc-{Guid.NewGuid():N}.db");
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
                services.AddScoped<RecipesManage.Application.Contracts.IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
            })
            .Build();

        Guid batchId;
        using (var boot = host.Services.CreateAsyncScope())
        {
            var db = boot.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();

            var stepId = Guid.NewGuid();
            var snapshot = new ControlRecipeSnapshot
            {
                MasterRecipeId = Guid.NewGuid(),
                RecipeVersionId = Guid.NewGuid(),
                RecipeCode = "CONC",
                RecipeName = "并发测试配方",
                ProductCode = "P",
                ProductName = "part",
                FrozenAt = DateTimeOffset.UtcNow,
                Steps =
                [
                    new SnapshotStep
                    {
                        StepId = stepId, Code = "S10", Name = "升温", Type = StepType.Heat,
                        Ordinal = 0, WatchdogSeconds = 60
                    }
                ],
                Edges = []
            };

            var batch = ProductionBatch.Create("BCONC1", Guid.NewGuid(), snapshot, "{}", Guid.NewGuid());
            batch.StepExecutions.Add(new BatchStepExecution(batch.Id, stepId, "S10", "升温", StepType.Heat, 0));
            batch.Queue();
            db.Batches.Add(batch);
            await db.SaveChangesAsync();
            batchId = batch.Id;
        }

        try
        {
            await using var a = host.Services.CreateAsyncScope();
            await using var b = host.Services.CreateAsyncScope();
            var dbA = a.ServiceProvider.GetRequiredService<AppDbContext>();
            var dbB = b.ServiceProvider.GetRequiredService<AppDbContext>();

            // 两个写者各自加载同一批次（模拟 HTTP 请求与调度线程各持一份副本）。
            var batchA = await dbA.Batches.SingleAsync(x => x.Id == batchId);
            var batchB = await dbB.Batches.SingleAsync(x => x.Id == batchId);

            batchA.MarkRunning(DateTimeOffset.UtcNow);
            await dbA.SaveChangesAsync();

            // B 仍持有过期的副本：它的写必须被并发令牌拦下，而不是覆盖 A 的 Running。
            batchB.Hold("调度侧保持");
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());

            await using var verify = host.Services.CreateAsyncScope();
            var dbV = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            var persisted = await dbV.Batches.AsNoTracking().SingleAsync(x => x.Id == batchId);
            Assert.Equal(BatchStatus.Running, persisted.Status);
        }
        finally
        {
            host.Dispose();
            try { File.Delete(dbPath); } catch { /* temp db */ }
        }
    }
}
