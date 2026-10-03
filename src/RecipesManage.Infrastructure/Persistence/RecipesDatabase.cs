using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace RecipesManage.Infrastructure.Persistence;

/// <summary>
/// 数据访问层唯一提供方：SQLite。
/// PostgreSQL 支持已于 2026-09 移除（历史迁移里的 PG 分支保留，只是不再执行）。
/// 注意：SQLite 对 DateTimeOffset 列的 ORDER BY 会抛 NotSupportedException，
/// 日期排序/分页只能留在客户端；多实例部署不受支持（单文件库 + 进程内调度队列）。
/// </summary>
public static class RecipesDatabase
{
    public const string Sqlite = "sqlite";

    public static string ResolveConnectionString(string? sqlite) =>
        string.IsNullOrWhiteSpace(sqlite) ? "Data Source=App_Data/recipes.db" : sqlite.Trim();

    public static string HealthName(DatabaseFacade database) => Sqlite;

    public static string ControlRecipeStorage(string provider) => "TEXT";

    /// <summary>
    /// 健康检查的应答体。带上进程身份是看门狗需要的最小事实：
    /// "探活通过但 pid 变了"意味着刚被重启过，而界面上一切正常。
    /// version / migration 是给远程支持用的：现场第一句话要能问出"你装的是哪版、库结构升到哪儿了"，
    /// 而不是让人去翻 exe 属性或者猜有没有装过某条迁移。
    /// </summary>
    public static object HealthBody(string provider, bool ok, MigrationWatermark? schema = null) => ok
        ? new
        {
            status = "ok", database = Sqlite, engine = "brmes", controlRecipe = "TEXT",
            version = Version, pid = Environment.ProcessId, startedAtUtc = ProcessStartedAtUtc,
            migration = schema?.Applied, pendingMigrations = schema?.PendingCount
        }
        : new
        {
            status = "unhealthy", database = Sqlite, controlRecipe = "TEXT",
            version = Version, pid = Environment.ProcessId, startedAtUtc = ProcessStartedAtUtc
        };

    /// <summary>产品版本：出包时用 <c>-p:Version=x.y.z</c> 覆盖，提交号由 SourceRevisionId 拼进来。</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(RecipesDatabase).Assembly;
            var informational = assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(informational)
                ? assembly.GetName().Version?.ToString() ?? "unknown"
                : informational;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 同 pid 的取舍：/health 是看门狗唯一依赖的端点，绝不能因为诊断信息而 500。
            return "unknown";
        }
    }

    /// <summary>
    /// 库结构水位：已应用的最后一条迁移 + 还没应用几条。
    /// 正常启动后 pending 恒为 0（开机就 Migrate），非 0 意味着升级半途失败——那正是需要立刻知道的形态。
    /// </summary>
    public sealed record MigrationWatermark(string? Applied, int PendingCount);

    public static async Task<MigrationWatermark?> SafeMigrationWatermarkAsync(
        AppDbContext db, CancellationToken ct = default)
    {
        try
        {
            var applied = await db.Database.GetAppliedMigrationsAsync(ct);
            var pending = await db.Database.GetPendingMigrationsAsync(ct);
            var last = applied.LastOrDefault();
            var count = pending.Count();
            return new MigrationWatermark(string.IsNullOrEmpty(last) ? null : last, count);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;      // 拿不到就不报这两个字段，别把 /health 打成 503
        }
    }

    private static readonly DateTimeOffset ProcessStartedAtUtc = SafeProcessStartUtc();

    private static DateTimeOffset SafeProcessStartUtc()
    {
        try
        {
            return new DateTimeOffset(
                System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            // 拿不到就少一个字段，绝不能让 /health 因为诊断信息而 500——那是看门狗唯一依赖的端点。
            return DateTimeOffset.UnixEpoch;
        }
    }

    public static string? TryGetSqliteFilePath(string? sqlite)
    {
        var path = ToDataSource(ResolveConnectionString(sqlite));
        if (IsMemoryPath(path))
            return null;
        return Path.GetFullPath(path);
    }

    private static string ToDataSource(string connectionString) =>
        connectionString.Replace("Data Source=", "", StringComparison.OrdinalIgnoreCase).Trim();

    private static bool IsMemoryPath(string path) =>
        path.Contains(":memory:", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 用 SQLite 的 Backup API 导出一份<strong>一致</strong>的快照到临时文件，返回路径（调用方负责删）。
    ///
    /// 为什么不能直接把 .db 拷走：库跑在 WAL 模式下，尚未合并进主库的提交还在 <c>-wal</c> 文件里，
    /// 直接复制得到的是"主库 + 半截 WAL"的撕裂快照 —— 恢复出来可能丢最近的事务，甚至打不开。
    /// Backup API 会在持锁的情况下按页复制，并把 WAL 一并收敛进目标库。
    /// </summary>
    public static string BackupToTempFile(string? sqlite)
    {
        var source = TryGetSqliteFilePath(sqlite)
            ?? throw new InvalidOperationException("当前不是可备份的 SQLite 文件库（内存库不支持）。");
        if (!File.Exists(source))
            throw new FileNotFoundException("找不到 SQLite 文件。", source);

        var target = Path.Combine(Path.GetTempPath(), $"brmes-backup-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.db");
        // Pooling=False 是必须的：Microsoft.Data.Sqlite 默认连接池在 Dispose 后仍持有文件句柄，
        // 调用方接着去读这个备份文件就会 IOException「being used by another process」（实测踩过）。
        // 用嵌套 using 而不是 using var，保证返回前句柄已经真的释放。
        using (var src = new SqliteConnection($"Data Source={source};Pooling=False"))
        using (var dst = new SqliteConnection($"Data Source={target};Pooling=False"))
        {
            src.Open();
            dst.Open();
            src.BackupDatabase(dst);
        }

        return target;
    }

    public static void Apply(DbContextOptionsBuilder options, string? sqlite)
    {
        var cs = ResolveConnectionString(sqlite);
        EnsureSqliteDirectory(cs);
        options.UseSqlite(cs, s => s.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
        options.AddInterceptors(new SqliteConnectionPragmas());
    }

    public static void EnsureSqliteDirectory(string connectionString)
    {
        var path = ToDataSource(connectionString);
        if (IsMemoryPath(path))
            return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
