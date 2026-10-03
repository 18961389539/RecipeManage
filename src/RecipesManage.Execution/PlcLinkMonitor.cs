using System.Net.Sockets;

namespace RecipesManage.Execution;

/// <summary>
/// 一条车道到 PLC 的"读通道"健康度。只做记账，不碰 PLC、不碰状态机：
/// 读失败时是否还在容忍窗口内、恢复后中断了多久，都从这里取。
///
/// 窗口从**上一次读成功**起算，而不是从第一次失败起算：一次读超时本身就要挂 8 秒，
/// 这 8 秒上位机同样什么都没看见，必须计入。
/// </summary>
internal sealed class PlcLinkMonitor
{
    private DateTimeOffset _lastSuccessAt;
    private bool _degraded;

    public PlcLinkMonitor(DateTimeOffset now) => _lastSuccessAt = now;

    public bool Degraded => _degraded;

    /// <summary>已连续失败的次数（恢复后清零）。</summary>
    public int Failures { get; private set; }

    /// <summary>记一次读失败。返回 true = 容忍窗口已用尽，调用方必须停下并报故障。</summary>
    public bool OnFailure(DateTimeOffset now, TimeSpan tolerance, out bool firstFailure)
    {
        firstFailure = !_degraded;
        _degraded = true;
        Failures++;
        return now - _lastSuccessAt >= tolerance;
    }

    /// <summary>记一次读成功。若此前处于失败状态，返回读不到的总时长，否则返回 null。</summary>
    public TimeSpan? OnSuccess(DateTimeOffset now)
    {
        TimeSpan? gap = _degraded ? now - _lastSuccessAt : null;
        _degraded = false;
        Failures = 0;
        _lastSuccessAt = now;
        return gap;
    }

    public TimeSpan Outage(DateTimeOffset now) => now - _lastSuccessAt;
}

/// <summary>读失败的分类：哪些算"线路问题，值得等一等"，哪些是配置/代码错误，等也没用。</summary>
internal static class PlcReadErrors
{
    /// <summary>
    /// 驱动把连不上 / 读失败统一抛成 <see cref="InvalidOperationException"/>（见 PlcDrivers.Ensure），
    /// 8 秒 IO 超时抛 <see cref="TimeoutException"/>，套接字层是 <see cref="IOException"/> / <see cref="SocketException"/>。
    /// 取消不是通讯故障；点表地址错、协议未支持这类 ArgumentException / NotSupportedException 也不是——
    /// 它们等多久都不会好，应当立刻暴露。
    /// </summary>
    public static bool IsTransient(Exception ex)
    {
        if (ex is OperationCanceledException)
            return false;
        var root = ex.GetBaseException();
        return root is TimeoutException or IOException or SocketException
            or InvalidOperationException or ObjectDisposedException;
    }
}

/// <summary>读 PLC 持续失败超过容忍窗口。携带的消息会原样进入批次故障原因。</summary>
internal sealed class PlcCommLostException(string message, Exception? inner = null)
    : Exception(message, inner);
