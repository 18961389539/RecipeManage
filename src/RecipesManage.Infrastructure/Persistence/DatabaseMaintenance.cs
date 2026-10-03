using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RecipesManage.Infrastructure.Persistence;

public sealed record MaintenanceSettings
{
    /// <summary>总开关。关掉只是不再做维护，不影响备份与业务读写。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>是否允许 VACUUM。关掉就只跑 PRAGMA optimize（廉价、不改文件）。</summary>
    public bool Vacuum { get; init; } = true;

    /// <summary>空闲页占比达到多少才值得重写整个库文件。</summary>
    public double MinFreeRatio { get; init; } = 0.2;

    /// <summary>并且至少白占多少兆。小库永远不值得 VACUUM，哪怕占比很高。</summary>
    public int MinFreeMegabytes { get; init; } = 64;

    /// <summary>遇到别的写者时最多等多久。VACUUM 不能在事务里跑，等不到就本轮放弃。</summary>
    public int BusyTimeoutMs { get; init; } = 30_000;
}

/// <summary>一轮维护的结果。<see cref="SkippedReason"/> 非空表示 VACUUM 没做以及为什么。</summary>
public sealed record MaintenanceResult(
    bool Optimized,
    bool Vacuumed,
    long BytesBefore,
    long BytesAfter,
    long FreePagesBefore,
    long TotalPages,
    long PageSize,
    string? SkippedReason)
{
    public long ReclaimedBytes => Math.Max(0, BytesBefore - BytesAfter);

    public string Describe()
    {
        var head = string.Format(CultureInfo.InvariantCulture, "PRAGMA optimize{0}", Optimized ? " 已执行" : " 失败");
        // 空闲页只在真的量过之后才说：被"批次在跑"这类前置条件挡住时根本没读 pragma，
        // 那时写"空闲 0 / 0 页"会让人以为库很健康，而事实是"没测"。
        if (TotalPages > 0 && PageSize > 0)
        {
            head += string.Format(CultureInfo.InvariantCulture, " · 空闲 {0} / {1} 页（{2:P0}）",
                FreePagesBefore, TotalPages, (double)FreePagesBefore / TotalPages);
        }
        return Vacuumed
            ? string.Format(CultureInfo.InvariantCulture, "{0} · VACUUM 回收 {1:N0} B", head, ReclaimedBytes)
            : string.Format(CultureInfo.InvariantCulture, "{0} · VACUUM 未执行：{1}", head, SkippedReason ?? "未启用");
    }
}

/// <summary>
/// SQLite 的日常维护：分析统计信息（<c>PRAGMA optimize</c>）与回收空闲页（<c>VACUUM</c>）。
///
/// 为什么需要：过程样本每约 400ms 每个测点一行，一个班次就是十几万行；SQLite 删行只把页挂到
/// freelist 上，<strong>文件永远不缩</strong>。单机现场盘不会扩容，几年后先出问题的就是磁盘。
/// <c>PRAGMA optimize</c> 是另一件事：让查询规划器按当前数据分布重算统计（SQLite 不会自动 ANALYZE），
/// 索引用得对不对会随数据倾斜变化。
///
/// 为什么用裸连接而不是 DbContext：VACUUM <strong>不能在事务里执行</strong>，而 EF 的连接可能正带着
/// 一个未提交的事务；自己开一条 <c>Pooling=False</c> 的连接有完整控制权，也顺带避开连接池
/// 在 Dispose 后仍握着文件句柄的老问题（备份那边实测踩过）。
/// </summary>
public sealed class DatabaseMaintenance
{
    private readonly MaintenanceSettings _settings;
    private readonly string? _databasePath;
    private readonly string _connectionString;

    /// <param name="sqliteConnectionString">null = 未配置，落到默认库路径（与 <see cref="DatabaseBackup"/> 一致）。</param>
    public DatabaseMaintenance(string? sqliteConnectionString, MaintenanceSettings settings)
    {
        _settings = settings;
        _connectionString = RecipesDatabase.ResolveConnectionString(sqliteConnectionString);
        _databasePath = RecipesDatabase.TryGetSqliteFilePath(sqliteConnectionString);
    }

    public MaintenanceSettings Settings => _settings;

    /// <summary>
    /// 跑一轮维护。<paramref name="idleForVacuum"/> 由调用方判断"现在有没有批次在跑"——
    /// 这个信息在应用层，不该由基础设施去猜。
    /// </summary>
    public MaintenanceResult Run(bool idleForVacuum)
    {
        if (_databasePath is null)
            return new MaintenanceResult(false, false, 0, 0, 0, 0, 0, "内存库没有可维护的文件");

        var before = new FileInfo(_databasePath).Length;
        long freePages = 0;
        long totalPages = 0;
        long pageSize = 0;
        var optimized = false;
        var vacuumed = false;
        string? skipped = null;

        using var connection = new SqliteConnection($"{_connectionString};Pooling=False");
        connection.Open();
        Execute(connection, $"PRAGMA busy_timeout={_settings.BusyTimeoutMs}");

        // optimize 排第一且无条件：它不改文件、耗时以毫秒计，VACUUM 成不成功都用得上。
        try
        {
            Execute(connection, "PRAGMA optimize");
            optimized = true;
        }
        catch (SqliteException)
        {
            skipped = "PRAGMA optimize 失败";
        }

        if (!_settings.Vacuum)
        {
            skipped = "Maintenance:Vacuum=false";
        }
        else if (!idleForVacuum)
        {
            // 有批次在跑就不动：VACUUM 要把整个库重写一遍，期间引擎每秒两次的事务会被挡，
            // 而"并发写入持续冲突"在现在的实现里会把批次置成故障。
            skipped = "有批次在运行/排队/保持/故障";
        }
        else
        {
            freePages = Scalar(connection, "PRAGMA freelist_count");
            totalPages = Scalar(connection, "PRAGMA page_count");
            pageSize = Scalar(connection, "PRAGMA page_size");
            if (WorthVacuuming(freePages, totalPages, pageSize, _settings))
            {
                Execute(connection, "VACUUM");
                vacuumed = true;
                // WAL 模式下 VACUUM 是整库重写进 WAL：不收回的话 -wal 文件会涨到和库一样大并一直留着，
                // 磁盘占用反而翻倍。此刻没有批次在跑，截断检查点不会挡住谁。
                // 回滚日志模式下这条是无操作，所以不必判断当前模式。
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            }
            else
            {
                skipped = "空闲页不足阈值";
            }
        }

        return new MaintenanceResult(
            optimized, vacuumed, before, new FileInfo(_databasePath).Length,
            freePages, totalPages, pageSize, skipped);
    }

    /// <summary>
    /// 值不值得重写整个文件。两个门槛都要过：占比（<see cref="MaintenanceSettings.MinFreeRatio"/>）
    /// 和绝对量（<see cref="MaintenanceSettings.MinFreeMegabytes"/>）——后者挡住"10 MB 的库白忙一场"。
    /// </summary>
    public static bool WorthVacuuming(long freePages, long totalPages, long pageSize, MaintenanceSettings settings)
    {
        if (freePages <= 0 || totalPages <= 0 || pageSize <= 0) return false;
        var freeBytes = freePages * pageSize;
        if (freeBytes < (long)settings.MinFreeMegabytes * 1024 * 1024) return false;
        return (double)freePages / totalPages >= settings.MinFreeRatio;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
