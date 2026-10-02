using Microsoft.Extensions.Logging;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Equipment;
using RecipesManage.Application.Services;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 连接退避。这里守的是两条相反的边界：一次抖动不该把批次判死，
/// 但重试也绝不能超出预算把工步看门狗的时间吃掉，更不能顺手变成"对着 PLC 盲写"。
/// </summary>
public sealed class PlcConnectRetryTests
{
    private static readonly EquipmentLine Line = new(
        "MB-01", "环回从站", PlcProtocol.ModbusTcp, "127.0.0.1", 1502, "MODBUS", 0, 1, "{}", "测试");

    /// <summary>测试用退避表：两次、各 1ms。默认表另有专条断言守它的预算。</summary>
    private static readonly IReadOnlyList<TimeSpan> Fast =
        [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)];

    [Fact]
    public async Task ATransientBlipRecoversWithoutFaultingAnything()
    {
        var created = new List<FlakyPlc>();
        var log = new RecordingLogger();
        // 前两次连接抛错、第三次成功：这正是"交换机抖了一下"的形状。
        var plan = new FailurePlan(failures: 2);

        var client = (FlakyPlc)await PlcConnectRetry.ConnectAsync(
            () => { var plc = new FlakyPlc(plan); created.Add(plc); return plc; }, Line, log, default, Fast);

        Assert.Same(created[^1], client);
        Assert.Equal(3, created.Count);
        // 失败的那两次连接对象要被释放，否则重试就是每次泄漏一个 socket。
        Assert.True(created[0].Disposed);
        Assert.True(created[1].Disposed);
        Assert.False(client.Disposed);
        Assert.Equal(2, log.Lines.Count(l => l.StartsWith("WRN", StringComparison.Ordinal)));
        Assert.Contains(log.Lines, l => l.Contains("第 3 次连接尝试后成功"));
    }

    [Fact]
    public async Task ItGivesUpAtTheEndOfTheBackoffAndKeepsTheRealReason()
    {
        var created = new List<FlakyPlc>();
        var log = new RecordingLogger();
        var plan = new FailurePlan(alwaysFail: true);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => PlcConnectRetry.ConnectAsync(
            () => { var plc = new FlakyPlc(plan); created.Add(plc); return plc; },
            Line, log, default, Fast));

        Assert.Equal("Modbus 连接失败", e.Message);
        // 上限 = 首次 + 退避表长度，不会越试越久。
        Assert.Equal(1 + Fast.Count, created.Count);
        Assert.All(created, c => Assert.True(c.Disposed));
        Assert.Contains(log.Lines, l => l.StartsWith("ERR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationIsNotTreatedAsAJitter()
    {
        var created = new List<FlakyPlc>();
        var log = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var plan = new FailurePlan(alwaysFail: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlcConnectRetry.ConnectAsync(
            () => { var plc = new FlakyPlc(plan, cancelOnConnect: true); created.Add(plc); return plc; },
            Line, log, cts.Token, Fast));

        // 进程在停 / 批次被中止 —— 重试只会把停机拖长，所以一次就够。
        Assert.Single(created);
        Assert.True(created[0].Disposed);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void TheDefaultBackoffIsBoundedWellInsideAStepWatchdog()
    {
        // 预算是这条防线的全部意义：重试总时长必须远小于任何一条工步看门狗，
        // 否则"多试两下"会把批次拖成超时故障，那比直接失败更糟。
        var total = PlcConnectRetry.DefaultBackoff.Aggregate(TimeSpan.Zero, (sum, t) => sum + t);
        Assert.True(total <= TimeSpan.FromSeconds(3), $"默认退避累计 {total}，不该超过 3 秒");
        Assert.True(PlcConnectRetry.DefaultBackoff.Count <= 3);
        Assert.True(PlcConnectRetry.DefaultBackoff[0] < PlcConnectRetry.DefaultBackoff[^1]);   // 是退避，不是等距
    }

    [Fact]
    public async Task ASuccessfulFirstConnectCostsNothingExtra()
    {
        var created = new List<FlakyPlc>();
        var plan = new FailurePlan(failures: 0);
        var log = new RecordingLogger();

        var client = await PlcConnectRetry.ConnectAsync(
            () => { var plc = new FlakyPlc(plan); created.Add(plc); return plc; }, Line, log, default, Fast);

        // 仿真协议永远连得上（ConnectAsync 是 no-op），所以这条路径必须一次成功、零日志噪音。
        Assert.Single(created);
        Assert.Same(created[0], client);
        Assert.Empty(log.Lines);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(level switch
            {
                LogLevel.Warning => "WRN ",
                LogLevel.Error => "ERR ",
                LogLevel.Information => "INF ",
                _ => "OTH "
            } + formatter(state, exception));
    }
}

/// <summary>
/// 每次重试都会<strong>新建</strong>一个客户端，所以"失败几次"必须记在共享的桶里，
/// 而不是记在某个实例上——否则测试会以为自己验了重试，其实一直在数同一个对象。
/// </summary>
file sealed class FailurePlan(int failures = 0, bool alwaysFail = false)
{
    private int _remaining = failures;

    public bool ShouldFail() => alwaysFail || _remaining-- > 0;
}

/// <summary>
/// 只用来验连接策略：任何写操作都被它直接拒掉，这样"重试顺手写了 PLC"这类回归会在测试里炸，
/// 而不是在设备上。
/// </summary>
file sealed class FlakyPlc(FailurePlan plan, bool cancelOnConnect = false) : IPlcHandshakeClient
{
    public bool Disposed { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (cancelOnConnect) throw new OperationCanceledException(cancellationToken);
        if (plan.ShouldFail()) return Task.FromException(new InvalidOperationException("Modbus 连接失败"));
        return Task.CompletedTask;
    }

    public Task<RecipesManage.Domain.Handshake.PlcInboundSignals> ReadSignalsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new RecipesManage.Domain.Handshake.PlcInboundSignals(
            true, false, false, false, 0, 0, false));

    public Task<RecipesManage.Domain.Handshake.PlcStepPayload> ReadStepPayloadAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("连接重试不该读参数区");

    public Task WriteStepPayloadAsync(int stepId, int stepType, IReadOnlyList<float> parameters,
        CancellationToken cancellationToken) => throw new NotSupportedException("重试不许写 PLC");

    public Task SetTriggerWriteAsync(bool value, CancellationToken cancellationToken) =>
        throw new NotSupportedException("重试不许写 PLC");

    public Task SetHostHoldAsync(bool value, CancellationToken cancellationToken) =>
        throw new NotSupportedException("重试不许写 PLC");

    public Task ResetCompleteAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("重试不许写 PLC");

    public Task<IReadOnlyDictionary<string, double>> ReadMeasuredAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
