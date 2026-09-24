using Microsoft.EntityFrameworkCore;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// <see cref="StepOutcome"/> 在库里必须是**枚举名的 TEXT**，不是整数序号。
///
/// 域侧换成枚举后，EF 默认会把它映射成 INTEGER —— 那会把既有批次静默读成另一个结论
/// （0 恰好是 Pending），也会让 batch_step_executions / batch_lanes 这两张审计相邻表的历史值失去意义。
/// AppDbContext 里那两处 HasConversion&lt;string&gt; 就是为此，改回去不会有任何编译错误，所以单独钉住。
/// </summary>
public sealed class StepOutcomeStorageTests
{
    [Fact]
    public async Task Outcome_Columns_StoreTheEnumMemberNameAsText()
    {
        await using var db = ServiceHarness.OpenDb("brmes-outcome");
        var (batchId, _) = await SchedulerHarness.SeedApprovedBatchAsync(
            db, "HT-OTXT", "ITG-OTXT", "BGT-OTXT",
            draftId =>
            [
                SchedulerHarness.Step(draftId, "S10", "heat", StepType.Heat, 0,
                    new SchedulerHarness.Param("目标温度", "℃", 120, 100, 200)),
                SchedulerHarness.Step(draftId, "S20", "hold", StepType.Hold, 1,
                    new SchedulerHarness.Param("保温时长", "s", 1, 0.5, 5))
            ]);

        var batch = await db.Batches.Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId);
        var first = batch.StepExecutions.Single(s => s.StepCode == "S10");
        first.MarkSkipped("文本口径校验");
        var lane = new BatchLane(batchId, batch.EquipmentId, "HT-OTXT", "UP-01 热处理单元");
        lane.Update("AwaitingConfirm", StepOutcome.AwaitingConfirm, first.StepId, "S10");
        db.Lanes.Add(lane);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(StepOutcome.Skipped,
            (await db.Batches.Include(b => b.StepExecutions).SingleAsync(b => b.Id == batchId))
            .StepExecutions.Single(s => s.StepCode == "S10").Outcome);
        Assert.Equal(StepOutcome.AwaitingConfirm,
            (await db.Lanes.AsNoTracking().SingleAsync(l => l.BatchId == batchId)).Outcome);

        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            Assert.Equal("Skipped", await ScalarAsync(conn,
                "select outcome from batch_step_executions where StepCode = 'S10'"));
            Assert.Equal("TEXT", await ScalarAsync(conn,
                "select \"type\" from pragma_table_info('batch_step_executions') where lower(name) = 'outcome'"));
            Assert.Equal("AwaitingConfirm", await ScalarAsync(conn,
                "select outcome from batch_lanes where BatchId = @batch", ("@batch", (object)batchId)));
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private static async Task<string?> ScalarAsync(
        System.Data.Common.DbConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result);
    }
}
