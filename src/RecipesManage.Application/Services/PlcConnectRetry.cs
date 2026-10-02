using Microsoft.Extensions.Logging;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Equipment;

namespace RecipesManage.Application.Services;

/// <summary>
/// 连不上 PLC 时的有界退避重试。
///
/// 只重试<strong>连接</strong>，不重试任何写：连接失败不会在 PLC 侧留下半截状态，重试是幂等的；
/// 而 <c>Trigger_Write</c> / 参数区写一旦失败，原因可能是"已经写进去了但回包丢了"，
/// 重放就是盲写——四步握手的全部意义就在于宿主不允许自己这么猜。
/// 同理读探测的重连也只做"重连 + 重读"，绝不在失败后补写任何东西。
///
/// 为什么必须有：以前 <c>ConnectAsync</c> 首次失败就抛穿，一次交换机抖动、一个还没起来的
/// OPC UA 服务器，都会让批次在还没动之前就进 <c>Faulted</c>，然后由人去点"重试"。
/// 预算封顶（默认 4 次、累计约 2.3 秒）是刻意的：真断链要靠这个上限止住，
/// 而不是让一个批次的启动把工步看门狗的时间吃光。
/// </summary>
public static class PlcConnectRetry
{
    /// <summary>每次重试之前的等待。长度就是额外尝试次数；总预算 = 这些间隔之和 + 每次连接自身的超时。</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultBackoff =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1500)];

    /// <summary>
    /// 每次尝试都<strong>新建</strong>一个客户端：失败的驱动对象（IoTClient / OPC UA 会话）不保证还能再用，
    /// 复用会把"重试"变成"重复撞同一个坏句柄"。
    /// </summary>
    public static async Task<IPlcHandshakeClient> ConnectAsync(
        Func<IPlcHandshakeClient> create,
        EquipmentLine equipment,
        ILogger log,
        CancellationToken ct,
        IReadOnlyList<TimeSpan>? backoff = null)
    {
        var retries = backoff ?? DefaultBackoff;

        for (var attempt = 0; ; attempt++)
        {
            var client = create();
            try
            {
                await client.ConnectAsync(ct);
                if (attempt > 0)
                    log.LogInformation(
                        "{Code} 在第 {Attempt} 次连接尝试后成功（协议 {Protocol}，{Host}:{Port}）。",
                        equipment.Code, attempt + 1, equipment.Protocol, equipment.Host, equipment.Port);
                return client;
            }
            catch (OperationCanceledException)
            {
                await SafeDisposeAsync(client, log);
                throw;           // 进程在停或批次被中止，不是链路问题，别拿它当抖动重试。
            }
            catch (Exception e)
            {
                await SafeDisposeAsync(client, log);
                if (attempt >= retries.Count)
                {
                    log.LogError(
                        e, "{Code} 连接失败，已按退避重试 {Attempts} 次仍不可用（协议 {Protocol}，{Host}:{Port}）。",
                        equipment.Code, attempt + 1, equipment.Protocol, equipment.Host, equipment.Port);
                    throw;
                }

                log.LogWarning(
                    "{Code} 第 {Attempt} 次连接失败，{Delay}ms 后重试：{Message}",
                    equipment.Code, attempt + 1, retries[attempt].TotalMilliseconds, e.Message);
                await Task.Delay(retries[attempt], ct);
            }
        }
    }

    private static async Task SafeDisposeAsync(IPlcHandshakeClient client, ILogger log)
    {
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception e)
        {
            // 半途而废的连接对象dispose 失败只是漏一个句柄，别让它盖掉真正的连接错误。
            log.LogDebug(e, "释放未连通的 PLC 客户端时出错，忽略。");
        }
    }
}
