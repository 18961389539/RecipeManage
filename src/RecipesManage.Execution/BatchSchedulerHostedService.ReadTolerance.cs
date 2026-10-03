using Microsoft.Extensions.Logging;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Handshake;

namespace RecipesManage.Execution;

/// <summary>
/// 读 PLC 失败的容忍。
///
/// 旧行为：握手轮询里任何一次读异常（含 8 秒 IO 超时）一路抛到会话外层，批次当场被置成 ENGINE 故障——
/// 而 PLC 在自己的程序里还跑得好好的，炉子并没有停。网络抖一下，一批热处理就要人工重新排队。
///
/// 现在：只对**读**容忍。失败期间
///   · 不推进状态机（没有新鲜读数就不做任何判断）、不写任何信号（"禁止盲写"的边界一寸没动）；
///   · 只重连（幂等）并重读，间隔 <see cref="HandshakeWatchdogOptions.ReadRetryInterval"/>；
///   · 窗口 <see cref="HandshakeWatchdogOptions.ReadFailureTolerance"/>（自上次读成功起算）用尽才报
///     <see cref="HandshakeFaultCode.PlcCommLost"/>；
///   · 恢复后由 <see cref="HandshakeStateMachine.NoteReadGap"/> 把心跳基准顺延，其余看门狗仍按真实时间走。
/// 写、触发、复位失败**不在**容忍之内：写没写成不可知，重发就是盲写。
/// </summary>
public sealed partial class BatchSchedulerHostedService
{
    /// <summary>
    /// 带容忍的单次读（用于不在轮询循环里的读：开工前的信号、写参回读、归档实测）。
    /// 窗口用尽抛 <see cref="PlcCommLostException"/>；取消与非线路类错误照常上抛。
    /// </summary>
    private async Task<T> ReadTolerantAsync<T>(
        LaneScope lane,
        SnapshotStep step,
        HandshakeStateMachine? machine,
        string what,
        Func<CancellationToken, Task<T>> read,
        CancellationToken ct)
    {
        var phase = machine?.Phase.ToString() ?? nameof(HandshakePhase.WaitingPlcReady);
        while (true)
        {
            try
            {
                var value = await read(ct);
                if (lane.Link.OnSuccess(DateTimeOffset.UtcNow) is { } gap)
                {
                    machine?.NoteReadGap(gap);
                    await OnReadRecoveredAsync(lane, step, phase, gap, ct);
                }

                return value;
            }
            catch (Exception ex) when (PlcReadErrors.IsTransient(ex))
            {
                if (await OnReadFailureAsync(lane, step, phase, what, ex, ct))
                    throw new PlcCommLostException(CommLostMessage(lane, what, ex), ex);
                await Task.Delay(lane.Watchdog.ReadRetryInterval, ct);
            }
        }
    }

    /// <summary>
    /// 记一次读失败并尝试重连。返回 true = 窗口已用尽，调用方必须停下并报 <see cref="HandshakeFaultCode.PlcCommLost"/>。
    /// </summary>
    private async Task<bool> OnReadFailureAsync(
        LaneScope lane, SnapshotStep step, string phase, string what, Exception ex, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var tolerance = lane.Watchdog.ReadFailureTolerance;
        if (lane.Link.OnFailure(now, tolerance, out var first))
            return true;

        if (first)
        {
            _log.LogWarning(ex,
                "批次 {BatchNo} 设备 {Equipment} 读 PLC（{What}）失败，进入通讯容忍窗口 {Tolerance}：不推进、不写入，只重连重读",
                lane.Batch.BatchNo, lane.Equipment.Code, what, tolerance);
            lane.Db.HandshakeEvents.Add(new HandshakeEvent(
                lane.Batch.Id, step.StepId, step.Code, phase, "comm",
                $"[{lane.Equipment.Code}] 读 PLC（{what}）失败，进入通讯容忍窗口 {tolerance.TotalSeconds:0}s：{DescribeFault(ex)}",
                null));
            await _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "comm", new
            {
                state = "degraded",
                equipmentCode = lane.Equipment.Code,
                stepCode = step.Code,
                toleranceSeconds = tolerance.TotalSeconds,
                reason = DescribeFault(ex)
            }), ct);
        }

        await ReconnectQuietlyAsync(lane, ct);
        return false;
    }

    private async Task OnReadRecoveredAsync(
        LaneScope lane, SnapshotStep step, string phase, TimeSpan gap, CancellationToken ct)
    {
        _log.LogInformation("批次 {BatchNo} 设备 {Equipment} 读 PLC 已恢复，中断 {Gap:0.0}s",
            lane.Batch.BatchNo, lane.Equipment.Code, gap.TotalSeconds);
        lane.Db.HandshakeEvents.Add(new HandshakeEvent(
            lane.Batch.Id, step.StepId, step.Code, phase, "comm",
            $"[{lane.Equipment.Code}] 读 PLC 已恢复，中断 {gap.TotalSeconds:0.0}s，按最新读数继续（期间未写入任何信号）", null));
        await _publisher.PublishAsync(new ExecutionEvent(lane.Batch.Id, "comm", new
        {
            state = "restored",
            equipmentCode = lane.Equipment.Code,
            stepCode = step.Code,
            gapSeconds = gap.TotalSeconds
        }), ct);
    }

    /// <summary>重连只是重新打开通道，不写任何点位，可重复。连不上就留给下一轮重读去判。</summary>
    private async Task ReconnectQuietlyAsync(LaneScope lane, CancellationToken ct)
    {
        try
        {
            await lane.Plc.ConnectAsync(ct);
        }
        catch (Exception e) when (PlcReadErrors.IsTransient(e))
        {
            _log.LogDebug(e, "设备 {Equipment} 通讯容忍期内重连失败，稍后重试", lane.Equipment.Code);
        }
    }

    private static string CommLostMessage(LaneScope lane, string what, Exception ex) =>
        $"读 PLC（{what}）已连续失败 {lane.Link.Outage(DateTimeOffset.UtcNow).TotalSeconds:0}s，" +
        $"超过容忍窗口 {lane.Watchdog.ReadFailureTolerance.TotalSeconds:0}s，已停止驱动本批次：{DescribeFault(ex)}";
}
