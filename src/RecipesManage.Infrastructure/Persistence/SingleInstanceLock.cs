using System.Globalization;

namespace RecipesManage.Infrastructure.Persistence;

/// <summary>
/// 单实例互斥：锁住"这个数据库文件正被一个进程驱动"这件事。
///
/// 为什么必须：调度状态在进程内（Channel + 会话字典），而 <c>RecoverRunningAsync</c> 会把
/// 每个正在跑的批次接管回**自己**的引擎。于是两个实例（Windows 服务 + 有人手工 dotnet run 看一眼，
/// 或看门狗重复拉起）会同时朝同一台 PLC 写参数、置 Trigger——这才是真正的危险，
/// 而 ConcurrencyStamp 的收敛守卫只会让其中一方在写了一半之后把批次打成故障。
///
/// 用文件锁而不是命名 Mutex 的三个理由：
/// - 身份天然对齐：锁的是"这个库"，不是"这台机器"。同一台机器上两份 checkout（各自的 App_Data）
///   仍能并行跑，而按机器全局加锁会把开发机挡死。
/// - 进程被硬杀时由操作系统释放，不留需要下次启动去猜死活的 PID 文件。
/// - 跨平台，且和 SQLite 自己的锁在同一套文件系统语义上（网络盘上 SQLite 能用它就能用）。
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    /// <summary>
    /// 因"已有实例"而退出时用的进程退出码（EX_TEMPFAIL 的惯例值）。
    /// 看门狗要靠它区分"这个实例被拒了，别重试"和"应用真的崩了，要拉起来"。
    /// </summary>
    public const int DuplicateInstanceExitCode = 75;

    private const string Suffix = ".lock";
    private FileStream? _stream;

    private SingleInstanceLock(string lockPath, string databasePath)
    {
        Path = lockPath;
        DatabasePath = databasePath;
    }

    public string Path { get; }
    public string DatabasePath { get; }
    public int Pid { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }

    /// <summary>
    /// 尝试持有锁。<c>null</c> 表示已经有别的进程在用同一个库（调用方必须拒绝启动）；
    /// 内存库（设计期工具、部分测试）没有可锁的文件，返回 null 但 <see cref="IsSkipped"/> 为真，调用方据此放行。
    /// </summary>
    public static SingleInstanceLock? TryAcquire(string? sqliteConnectionString, out bool skipped)
    {
        skipped = false;
        var database = RecipesDatabase.TryGetSqliteFilePath(sqliteConnectionString);
        if (database is null)
        {
            skipped = true;
            return null;
        }

        var path = database + Suffix;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // FileShare.Read：别人能读走"是谁持有锁"用于诊断，但不能再以读写打开——那就是抢锁。
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var lockHandle = new SingleInstanceLock(path, database)
            {
                Pid = Environment.ProcessId,
                StartedAtUtc = DateTimeOffset.UtcNow
            };
            lockHandle._stream = stream;
            lockHandle.WriteOwner();
            return lockHandle;
        }
        catch (IOException)
        {
            // 独占打开失败 = 有活着的持有者。Windows 抛 IOException；换平台也落在这个分支。
            return null;
        }
    }

    public static SingleInstanceLock? TryAcquire(string? sqliteConnectionString) =>
        TryAcquire(sqliteConnectionString, out _);

    /// <summary>把"当前是谁持有这个库"写进锁文件，供后来者报错时引用。</summary>
    private void WriteOwner()
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"pid={Pid}\tstarted={StartedAtUtc:O}\tmachine={Environment.MachineName}\tdb={DatabasePath}\n");
        _stream!.Position = 0;
        _stream.SetLength(0);
        using var writer = new StreamWriter(_stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(text);
        writer.Flush();
        _stream.Flush(true);
    }

    /// <summary>读取持有者信息。文件可能被并发截断写，读不到就返回 null，不影响拒绝启动的决定。</summary>
    public static string? DescribeOwner(string? sqliteConnectionString)
    {
        var database = RecipesDatabase.TryGetSqliteFilePath(sqliteConnectionString);
        if (database is null) return null;
        try
        {
            // 只读 + FileShare.ReadWrite：持有者要往文件里写，这里不能挡它。
            using var stream = new FileStream(database + Suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var line = reader.ReadLine();
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        var stream = _stream;
        if (stream is null) return;
        _stream = null;
        stream.Dispose();
        // 故意不删锁文件：删了会开一个窗口——A 正在独占打开、B 看到文件不存在就以为没人在跑。
        // 留一个几十字节的文件，换来的是"锁的存在与否"永远不是判断依据，独占句柄才是。
    }
}
