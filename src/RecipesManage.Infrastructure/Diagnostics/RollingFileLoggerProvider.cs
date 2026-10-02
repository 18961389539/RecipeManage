using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RecipesManage.Infrastructure.Diagnostics;

/// <summary>
/// 落盘日志的配置。全部有默认值：现场常常没人改 appsettings，而"什么日志都没留下"这件事
/// 只会在真要查事故的时候才暴露，那时候已经补不回来了。
/// </summary>
public sealed record FileLogSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 日志目录。相对路径按进程的当前目录展开——服务形态下这条依赖 <c>--contentRoot</c>，
    /// 与 <c>ConnectionStrings:Sqlite</c> 是同一个坑（见 docs/deployment.md §3）。
    /// </summary>
    public string Directory { get; init; } = "App_Data/logs";

    /// <summary>这道 sink 自己的下限；框架的 <c>Logging:LogLevel</c> 规则照常叠加在上面。</summary>
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

    /// <summary>
    /// 保留天数。<c>0</c>（默认）不删：这台机器上"当时发生了什么"只有这一份证据，
    /// 真要腾盘应由人决定，而不是让日志组件每天悄悄烧掉一段历史。
    /// </summary>
    public int RetainedDays { get; init; }

    /// <summary>内存里排队的最大行数。满了以后丢新来的那一行并计数，绝不拿磁盘去堵业务线程。</summary>
    public int QueueCapacity { get; init; } = 20_000;
}

/// <summary>
/// 按 UTC 日期分文件的最小日志落盘。
///
/// 为什么不引一个现成的日志框架：这里需要的语义很少——按天分文件、别把进程拖崩、丢了要知道丢了多少，
/// 为一件事拖进整套结构化日志和它的配置语言不划算，而且这台机器上的其它存储策略（备份、维护）
/// 都是同样克制的自写件，加一个依赖得给出一份理由。
///
/// 现场要的不是"能远程看日志"，而是"能把一个文件带回来"：进程崩过、被看门狗拉起过、
/// 批次为什么进 Fault，都只在这份文件里留过痕迹。作为 Windows 服务运行时 console provider 等于没写。
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider, IDisposable
{
    private const string Prefix = "brmes-";
    private const string Suffix = ".log";

    private readonly FileLogSettings _settings;
    private readonly BlockingCollection<string> _queue;
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _writer;
    private int _disposed;
    private long _dropped;

    public RollingFileLoggerProvider(FileLogSettings settings)
    {
        _settings = settings;
        _queue = new BlockingCollection<string>(Math.Max(128, settings.QueueCapacity));
        _writer = Task.Run(DrainAsync);
    }

    /// <summary>因为队列满而没落盘的行数。日志可以丢，但"丢了"这件事本身必须留下数字。</summary>
    public long DroppedLines => Interlocked.Read(ref _dropped);

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName, _settings);

    internal void Enqueue(string line)
    {
        if (_closing.IsCancellationRequested) return;
        // TryAdd 而不是 Add：日志永远不该让业务线程等磁盘。
        if (!_queue.TryAdd(line)) Interlocked.Increment(ref _dropped);
    }

    /// <summary>日志目录的绝对路径。与备份目录同一套解析规则。</summary>
    public string PathRoot =>
        Path.IsPathRooted(_settings.Directory)
            ? _settings.Directory
            : Path.GetFullPath(_settings.Directory);

    private async Task DrainAsync()
    {
        StreamWriter? file = null;
        DateOnly? openDay = null;
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable(_closing.Token))
            {
                file ??= Open(DateOnly.FromDateTime(DateTime.UtcNow), ref openDay);
                file.WriteLine(line);
                file.Flush();     // 崩盘前那几行恰恰最要紧，这里的 buffering 省不得。
            }
        }
        catch (OperationCanceledException)
        {
            // 进程在停，走 finally 收尾。
        }
        catch (Exception)
        {
            // 磁盘满、目录被删、权限改动：写不进去就停这道 sink，
            // 其它 provider 照常工作，业务线程一根汗毛都不碰。
        }
        finally
        {
            // 把已经排队的尾巴写完——最后几条往往正是事故原因本身。
            while (_queue.TryTake(out var line))
            {
                try
                {
                    if (file is null) file = Open(DateOnly.FromDateTime(DateTime.UtcNow), ref openDay);
                    file.WriteLine(line);
                    file.Flush();
                }
                catch (Exception)
                {
                    break;
                }
            }

            try
            {
                file?.Flush();
                file?.Dispose();
            }
            catch (Exception)
            {
                // 收尾失败没意义向上抛：Dispose 在宿主停机的路径上。
            }
        }
    }

    private StreamWriter Open(DateOnly day, ref DateOnly? openedOn)
    {
        System.IO.Directory.CreateDirectory(PathRoot);
        openedOn = day;
        PruneIfConfigured(day);
        // FileShare.ReadWrite：允许运维用编辑器一边看一边写；Windows 上不放开就撞文件锁。
        var stream = new FileStream(
            Path.Combine(PathRoot, FileNameFor(day)), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>只在显式配了保留天数时才删旧文件；0 = 全留。</summary>
    private void PruneIfConfigured(DateOnly today)
    {
        if (_settings.RetainedDays <= 0) return;
        var cutoff = today.AddDays(-_settings.RetainedDays);
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(PathRoot, Prefix + "*" + Suffix))
            {
                if (TryParseDay(Path.GetFileName(path), out var day) && day < cutoff)
                    File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 裁不动无非是多留一份，不影响继续写。
        }
    }

    /// <summary>文件名里的日期是定宽 UTC，与备份命名、审计时间戳同一个口径。</summary>
    public static string FileNameFor(DateOnly day) =>
        $"{Prefix}{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{Suffix}";

    public static bool TryParseDay(string name, out DateOnly day)
    {
        day = default;
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Suffix, StringComparison.Ordinal))
            return false;
        var stem = name[Prefix.Length..^Suffix.Length];
        return DateOnly.TryParseExact(stem, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    public void Dispose()
    {
        // 幂等：Dispose 在宿主停机路径上，可能来自 LoggerFactory 的释放，也可能来自显式调用。
        // 第二次走进来时 CTS 已经释放，Cancel() 会抛 ObjectDisposedException —— 那会在最不该出错的
        // 收尾时刻把异常抛回调用方。
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _closing.Cancel();
        _queue.CompleteAdding();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));     // 给写线程一点时间排干，但不硬等。
        }
        catch (Exception)
        {
            // 同 Dispose 的一贯取向：不向上抛。
        }

        _queue.Dispose();
        _closing.Dispose();
    }
}

/// <summary>
/// 一行长这样：<c>2026-09-25 03:07:05.123Z [INF] RecipesManage.Api.HealthController — 消息</c>，
/// 异常缩进跟在下面。定宽 UTC 前缀是刻意的：字典序即时间序，两份日志对时间轴时不用换算。
/// </summary>
internal sealed class RollingFileLogger(
    RollingFileLoggerProvider sink, string category, FileLogSettings settings) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= settings.MinimumLevel;

    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;

        var head = $"{DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'")} [{Tag(level)}] {category}";
        if (eventId.Id != 0) head += $"({eventId.Id})";
        Write(head + " — " + formatter(state, exception));

        if (exception is not null)
            foreach (var line in Split(exception.ToString()))
                sink.Enqueue("    " + line);
    }

    private void Write(string text)
    {
        foreach (var line in Split(text)) sink.Enqueue(line);
    }

    private static IEnumerable<string> Split(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "CRT"
    };
}
