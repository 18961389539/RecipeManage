using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Batches;

/// <summary>跳步请求该走哪条路。</summary>
public enum SkipMode
{
    /// <summary>批次在跑：引擎正握着 PLC，只能把意图交给引擎，由它在安全相位上落实。</summary>
    ForwardToEngine,

    /// <summary>批次已保持/故障：引擎已让出 PLC，上位机直接把工步标成跳过并推进。</summary>
    ApplyOffline,
}

/// <summary>
/// 跳步的领域规则：跳哪个工步、现在能不能跳、跳了之后批次怎么走。
///
/// 为什么从 <c>BatchService.SkipAsync</c> 里拿出来：那个方法 70 多行里，规则、状态迁移、
/// 副作用（调度意图 / 审计 / 推送 / 入队）搅在一起，规则只能靠起一整套服务 + 数据库去测。
/// 规则本身是纯函数——给定批次、工步结论、车道行，结论只有"拒绝（带错误码）/转交引擎/离线直接跳"三种——
/// 拿出来之后每个分支都能直接单测，服务只留副作用。
///
/// 安全方向（沿用原行为，逐字保持错误码与文案）：fail-closed。
/// 在跑的批次只在 PLC_Ready / 等待 / 人工确认相位允许跳；没有车道行就按不可跳处理，
/// 宁可让操作员先保持再跳，也不拿展示串或历史事件去猜一个相位然后盲写 PLC。
/// </summary>
public static class StepSkip
{
    /// <summary>
    /// 定出要跳的工步：显式指定优先，其次批次当前工步，再次正在跑/等确认的工步，最后按当前序号兜底。
    /// 显式指定的工步不在快照里 → <c>SKIP_STEP</c>。
    /// </summary>
    public static SnapshotStep ResolveTarget(ProductionBatch batch, ControlRecipeSnapshot snapshot, Guid? stepId)
    {
        if (stepId is Guid id)
            return snapshot.Steps.FirstOrDefault(s => s.StepId == id)
                   ?? throw new DomainException("SKIP_STEP", "指定工步不在本批次控制配方快照中。");
        if (batch.CurrentStepId is Guid current)
        {
            var match = snapshot.Steps.FirstOrDefault(s => s.StepId == current);
            if (match is not null)
                return match;
        }

        var running = batch.StepExecutions.FirstOrDefault(s => s.Outcome is StepOutcome.Running or StepOutcome.AwaitingConfirm);
        if (running is not null)
            return snapshot.Steps.Single(s => s.StepId == running.StepId);

        var index = Math.Clamp(batch.CurrentStepIndex, 0, Math.Max(snapshot.Steps.Count - 1, 0));
        return snapshot.Steps[index];
    }

    /// <summary>
    /// 判定能不能跳、走哪条路；不能跳就抛带错误码的 <see cref="DomainException"/>，不改任何状态。
    /// </summary>
    /// <param name="exec">目标工步的执行记录。</param>
    /// <param name="lane">目标工步所在设备车道的行；批次还没被引擎接管时为 null。</param>
    public static SkipMode Decide(ProductionBatch batch, BatchStepExecution exec, BatchLane? lane)
    {
        if (batch.Status == BatchStatus.Running)
        {
            if (exec.Outcome is StepOutcome.Completed or StepOutcome.Skipped)
                throw new DomainException("SKIP_DONE", "当前工步已完成，不能跳过。");
            if (exec.Outcome is not (StepOutcome.Running or StepOutcome.AwaitingConfirm))
                throw new DomainException("SKIP_UNSAFE", "只能跳过正在等待 PLC_Ready、等待或人工确认的工步，禁止跨单元误跳邻道。");
            // 车道相位的唯一真源是 batch_lanes 行：引擎在 MarkRunning 之前就给每条绑定设备建行
            // （EnsureLaneRowsAsync），所以"在跑却没有行"只可能是数据被外力破坏。
            // 这时一律按不可跳处理——宁可让操作员先保持再跳，也不能拿展示串或历史事件猜一个相位去盲写 PLC。
            if (lane is null)
                throw new DomainException("SKIP_UNSAFE",
                    "该车道还没有被执行引擎接管，不能跳步。请等批次进入握手状态后再试。");
            if (!BatchLanes.IsSkipSafePhase(lane.Phase))
                throw new DomainException("SKIP_UNSAFE",
                    $"车道 {lane.EquipmentCode} 当前相位 {lane.Phase}，禁止跳步盲写。请先保持，待 PLC_Ready / 等待 / 人工确认后再跳过。");
            return SkipMode.ForwardToEngine;
        }

        if (batch.Status is not BatchStatus.Held and not BatchStatus.Faulted)
            throw new DomainException("CANNOT_SKIP", $"批次状态 {batch.Status} 不能跳步。");

        if (exec.Outcome is StepOutcome.Completed)
            throw new DomainException("SKIP_DONE", "当前工步已完成，不能跳过。");

        return SkipMode.ApplyOffline;
    }

    /// <summary>
    /// 离线跳步的状态迁移：工步标成跳过；全部工步都结束就收尾，否则推进到下一个未结束的工步并重新排队。
    /// </summary>
    /// <returns>整批是否因此完成。未完成时调用方需要把批次交回调度（入队）。</returns>
    public static bool ApplyOffline(
        ProductionBatch batch, ControlRecipeSnapshot snapshot, BatchStepExecution exec, string reason, DateTimeOffset now)
    {
        exec.MarkSkipped(reason);
        if (batch.StepExecutions.All(s => s.Outcome is StepOutcome.Completed or StepOutcome.Skipped))
        {
            batch.Complete(now);
            return true;
        }

        var next = snapshot.Steps.FirstOrDefault(s =>
            batch.StepExecutions.Single(e => e.StepId == s.StepId).Outcome
                is StepOutcome.Pending or StepOutcome.Running or StepOutcome.Faulted or StepOutcome.Held);
        if (next is not null)
            batch.AdvanceTo(next.StepId, snapshot.Steps.ToList().FindIndex(s => s.StepId == next.StepId));
        batch.Queue();
        return false;
    }
}
