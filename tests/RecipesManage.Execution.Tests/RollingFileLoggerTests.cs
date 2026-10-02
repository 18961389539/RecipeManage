using RecipesManage.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 落盘日志。这台机器上"当时发生了什么"只有这一份文件，所以它必须真的写出内容、
/// 必须不能因为磁盘出问题把宿主拖停，而且旧日志默认不能被它自己删掉。
/// </summary>
public sealed class RollingFileLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"brmes-filelog-{Guid.NewGuid():N}");

    public RollingFileLoggerTests() => Directory.CreateDirectory(_root);

    private FileLogSettings Settings(LogLevel floor = LogLevel.Information, int keepDays = 0) =>
        new() { Directory = _root, MinimumLevel = floor, RetainedDays = keepDays };

    private string TodayFile() => Path.Combine(_root, RollingFileLoggerProvider.FileNameFor(DateOnly.FromDateTime(DateTime.UtcNow)));

    private static void Write(ILogger logger, LogLevel level, string message, Exception? error = null) =>
        logger.Log(level, 0, message, error, (s, _) => s.ToString() ?? "");

    [Fact]
    public void InformationAndAboveLandInTodaysUtcNamedFile()
    {
        using var provider = new RollingFileLoggerProvider(Settings());
        var log = provider.CreateLogger("RecipesManage.Api.HealthController");

        Write(log, LogLevel.Debug, "这条不该进来");
        Write(log, LogLevel.Information, "批次 B-1 已启动");
        Write(log, LogLevel.Warning, "PLC 心跳 {Seconds}s 未回", new TimeoutException("心跳超时"));

        provider.Dispose();

        var text = File.ReadAllText(TodayFile());
        Assert.DoesNotContain("这条不该进来", text, StringComparison.Ordinal);
        Assert.Contains("[INF] RecipesManage.Api.HealthController — 批次 B-1 已启动", text, StringComparison.Ordinal);
        Assert.Contains("[WRN]", text, StringComparison.Ordinal);
        // 异常正文必须跟着那行走，否则"看门狗把进程拉起来了"这类事实就没有证据。
        Assert.Contains("TimeoutException", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LinesArePrefixedWithFixedWidthUtcSoTheySortAndInterleaveWithBackups()
    {
        using (var provider = new RollingFileLoggerProvider(Settings()))
            Write(provider.CreateLogger("Cat"), LogLevel.Error, "boom");

        var first = File.ReadLines(TodayFile()).First();
        // yyyy-MM-dd HH:mm:ss.fffZ —— 与审计时间戳、备份文件名同一个口径，跨文件对时间轴不用换算。
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z \[ERR\] Cat — boom$", first);
    }

    [Fact]
    public void MultiLineMessagesStayOnTheirOwnLines()
    {
        using var provider = new RollingFileLoggerProvider(Settings());
        Write(provider.CreateLogger("Cat"), LogLevel.Information, "第一行\r\n第二行");
        provider.Dispose();

        var lines = File.ReadAllLines(TodayFile());
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("第一行", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("第二行", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AWriteFailureCannotEscapeIntoTheBusinessThread()
    {
        // 把一个已存在的普通文件当日志目录用：建目录必然失败，等价于盘满或权限被改。
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "这是个文件，不是目录");
        using var provider = new RollingFileLoggerProvider(new FileLogSettings { Directory = blocked });
        var log = provider.CreateLogger("Cat");

        var e = Record.Exception(() =>
        {
            for (var i = 0; i < 50; i++) Write(log, LogLevel.Information, $"第 {i} 条");
            provider.Dispose();
        });
        Assert.Null(e);
    }

    [Fact]
    public void OldLogsSurviveUnlessTheOperatorAsksForRetention()
    {
        var ancient = Path.Combine(_root, "brmes-20260101.log");
        File.WriteAllText(ancient, "历史");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "别人的文件");

        using (var provider = new RollingFileLoggerProvider(Settings()))
            Write(provider.CreateLogger("Cat"), LogLevel.Information, "默认一个都不删");
        Assert.True(File.Exists(ancient));       // RetainedDays=0 → 不动历史

        using (var pruning = new RollingFileLoggerProvider(Settings(keepDays: 1)))
            Write(pruning.CreateLogger("Cat"), LogLevel.Information, "按天数裁");
        Assert.False(File.Exists(ancient));      // 只有显式配了保留天数才裁
        Assert.True(File.Exists(Path.Combine(_root, "notes.txt")));
    }

    [Fact]
    public void FileNamesAreItsOwnAndFixedWidthUtc()
    {
        Assert.Equal("brmes-20260925.log", RollingFileLoggerProvider.FileNameFor(new DateOnly(2026, 9, 25)));
        Assert.True(RollingFileLoggerProvider.TryParseDay("brmes-20260925.log", out var day));
        Assert.Equal(new DateOnly(2026, 9, 25), day);
        // 陌生文件名一律不认：裁剪只能删自己写的。
        Assert.False(RollingFileLoggerProvider.TryParseDay("brmes-2026-09-25.log", out _));
        Assert.False(RollingFileLoggerProvider.TryParseDay("recipes.log", out _));
        Assert.False(RollingFileLoggerProvider.TryParseDay("brmes-20260925.txt", out _));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录留在 %TEMP%，比让测试变红合适。
        }
    }
}
