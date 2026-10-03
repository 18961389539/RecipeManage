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
using RecipesManage.Simulation;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 引擎集成测试的共用工装：一台仿真设备 + 一份已批准的快照 + 一个排队批次。
///
/// 原来这段在 BatchSchedulerIntegrationTests 里逐条测试各写一份（文件因此涨到 1600 多行），
/// 新增"相位 × 请求"矩阵时再抄一遍就是第三份。参数与口令都是测试自己造的，不涉真实现场。
/// </summary>
internal static class SchedulerHarness
{
    public const string SimulatorTagMap = "{}";

    public static IHost CreateHost(string dbPath, ConcurrentBag<ExecutionEvent> events, SimulatedPlcRack? rack = null) =>
        Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(services =>
            {
                // 与生产同一条配置路径（含连接级 busy_timeout）：引擎测试要验证的就是生产的并发行为。
                services.AddDbContext<AppDbContext>(o => RecipesDatabase.Apply(o, $"Data Source={dbPath}"));
                services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
                services.AddScoped<EquipmentLeaseService>();
                services.AddSingleton(rack ?? new SimulatedPlcRack());
                services.AddPlcSimulation();
                services.AddSingleton<IPlcDriverFactory, PlcDriverFactory>();
                services.AddSingleton<IExecutionPublisher>(new ServiceHarness.CapturingPublisher(events));
                services.AddSingleton<BatchSchedulerHostedService>();
                services.AddSingleton<IBatchScheduler>(sp => sp.GetRequiredService<BatchSchedulerHostedService>());
                services.AddHostedService(sp => sp.GetRequiredService<BatchSchedulerHostedService>());
            })
            .Build();

    public static string NewDbPath(string prefix = "brmes") =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.db");

    public static void DisposeHost(string dbPath, IHost host)
    {
        try
        {
            host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch
        {
            // 关停超时不影响断言结论，测试已经跑完。
        }
        host.Dispose();
        // 库跑在 WAL 下：-wal / -shm 是同一个库的一部分，一起清，否则 %TEMP% 里会留下一堆孤儿。
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // 临时库
            }
        }
    }

    /// <summary>升温 + 保温两步：够用绝大多数回路测试，工步时长 1s。</summary>
    public static async Task<(Guid BatchId, Guid EquipmentId)> SeedTwoStepBatchAsync(
        AppDbContext db, string eqCode, string recipeCode, string batchNo)
    {
        return await SeedApprovedBatchAsync(db, eqCode, recipeCode, batchNo, draftId =>
        [
            Step(draftId, "S10", "heat", StepType.Heat, 0,
                new Param("目标温度", "℃", 120, 100, 200), new Param("时长", "s", 1, 0.5, 5)),
            Step(draftId, "S20", "hold", StepType.Hold, 1,
                new Param("保温温度", "℃", 120, 100, 200), new Param("保温时长", "s", 1, 0.5, 5))
        ]);
    }

    /// <summary>
    /// 建一份"已批准 + 已封存快照 + 已排队"的批次。步骤之间自动串成一条线，
    /// 需要并行单元或跨单元汇合的测试请自己写 edges。
    /// </summary>
    public static async Task<(Guid BatchId, Guid EquipmentId)> SeedApprovedBatchAsync(
        AppDbContext db,
        string eqCode,
        string recipeCode,
        string batchNo,
        Func<Guid, IReadOnlyList<RecipeStep>> stepsFactory)
    {
        var equipment = new EquipmentLine(
            eqCode, "test furnace", PlcProtocol.Simulator, "127.0.0.1", 102,
            "S7_1200", 0, 1, SimulatorTagMap, "it");
        db.Equipment.Add(equipment);
        var recipe = MasterRecipe.Create(recipeCode, "it", "P", "part", null, Guid.NewGuid());
        var draft = recipe.RequireDraft();
        var steps = stepsFactory(draft.Id).ToList();
        var edges = steps.Count < 2
            ? new List<RecipeEdge>()
            : steps.Zip(steps.Skip(1), (a, b) => new RecipeEdge(draft.Id, a.Id, b.Id)).ToList();
        draft.ReplaceProcedure(steps, edges);
        draft.Submit(DateTimeOffset.UtcNow, ApprovalChain.Standard);
        draft.Decide(Guid.NewGuid(), "主管", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        draft.Decide(Guid.NewGuid(), "质量", ApprovalDecision.Approved, "ok", DateTimeOffset.UtcNow);
        recipe.MarkApproved(draft);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();

        var snapshot = ControlRecipeSnapshotFactory.From(recipe, draft, DateTimeOffset.UtcNow);
        SnapshotIntegrity.Seal(snapshot, SnapshotJson.Options, out var json);
        var batch = ProductionBatch.Create(batchNo, equipment.Id, snapshot, json, Guid.NewGuid());
        foreach (var step in snapshot.Steps)
            batch.StepExecutions.Add(new BatchStepExecution(batch.Id, step.StepId, step.Code, step.Name, step.Type, step.Ordinal));
        batch.Queue();
        db.Batches.Add(batch);
        await db.SaveChangesAsync();
        return (batch.Id, equipment.Id);
    }

    /// <param name="Archive">显式覆盖"归档作质量判定"；null = 按工步类型推（写 PLC 的相才归档）。</param>
    public sealed record Param(string Name, string Unit, double Setpoint, double Min, double Max, bool? Archive = null);

    public static RecipeStep Step(
        Guid versionId,
        string code,
        string name,
        StepType type,
        int ordinal,
        params Param[] parameters)
    {
        // 归档需要实测来源，域层在提交审核时就会拦（QualityArchive.DemandArchivableSources）。
        // 上位机类工步（等待/人工确认/质检）根本不读 PLC 实测点，所以默认不归档 ——
        // 以前这里给每个参数都写 true，"确认意见"也被当成质量规格，只是没人校验过。
        var slots = parameters
            .Select((p, i) => new RecipeParameter(
                i, p.Name, p.Unit, p.Setpoint, p.Min, p.Max, true, p.Archive ?? ControlRecipeWritePlan.WritesToPlc(type)))
            .ToList();
        return new RecipeStep(versionId, code, name, type, ordinal, 0, 0, 30, null, slots);
    }

    public static string? EquipmentCodeOf(ExecutionEvent evt)
    {
        var property = evt.Payload?.GetType().GetProperty("equipmentCode");
        return property?.GetValue(evt.Payload) as string;
    }

    /// <summary>轮询到条件成立为止；返回最后一次观测值，超时不抛——由调用方断言。</summary>
    public static async Task<T> WaitUntilAsync<T>(
        IHost host,
        Guid batchId,
        Func<T, bool> done,
        Func<IServiceProvider, Task<T>> read,
        int timeoutMs = 12_000,
        int intervalMs = 100)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        T value = default!;
        do
        {
            await Task.Delay(intervalMs);
            using var scope = host.Services.CreateScope();
            value = await read(scope.ServiceProvider);
        }
        while (!done(value) && DateTime.UtcNow < deadline);
        return value;
    }

    public static Task<ProductionBatch?> ReadBatchAsync(IServiceProvider services, Guid batchId)
    {
        var db = services.GetRequiredService<AppDbContext>();
        return db.Batches.AsNoTracking().Include(b => b.StepExecutions).SingleOrDefaultAsync(b => b.Id == batchId);
    }

    public static Task<List<HandshakeEvent>> ReadHandshakeLogAsync(IServiceProvider services, Guid batchId)
    {
        var db = services.GetRequiredService<AppDbContext>();
        return db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == batchId).ToListAsync();
    }
}
