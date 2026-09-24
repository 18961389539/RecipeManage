using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace RecipesManage.Infrastructure.Persistence;

/// <summary>
/// 备份策略：每天几点（UTC）跑一次、留几份、落在哪个目录。
/// 全部有默认值，配置文件里不写也能跑——单设备现场常常没人改 appsettings。
/// </summary>
public sealed record BackupSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>每天触发的 UTC 时刻。选在本地夜班末尾、白班之前，避开握手高峰。</summary>
    public TimeOnly AtUtc { get; init; } = new(2, 15);

    /// <summary>保留份数。0 或负数视为"不裁剪"（见 <see cref="DatabaseBackup.PruneOldBackups"/>）。</summary>
    public int Keep { get; init; } = 7;

    /// <summary>相对内容根的路径；也接受绝对路径。</summary>
    public string Directory { get; init; } = "App_Data/backups";
}

public sealed record BackupFile(string Name, long Bytes, DateTimeOffset CreatedAt);

/// <summary>
/// SQLite 库的每日备份：一致性快照 + 按份数裁剪。
///
/// 为什么必须有：整个系统只有一个 .db 文件，审计履历、控制配方快照、电子签名全在里面。
/// 磁盘坏掉或者误 <c>DELETE</c> 就是全部记录一起没了——而 GMP 侧要求这些记录留到留存期。
/// 快照走 SQLite 的 Backup API（见 <see cref="RecipesDatabase.BackupToTempFile"/>），
/// WAL 模式下直接复制文件会得到撕裂的库。
///
/// 只备份、不清样：过程样本一行都不删（批记录要引用它们），库会一直长大，
/// 所以留几份历史快照比留几个月的样本更要紧。
/// </summary>
public sealed class DatabaseBackup
{
    private const string Prefix = "brmes-";
    private const string Suffix = ".db";
    private static readonly Regex BackupName = new(@"^brmes-\d{8}-\d{6}\.db$", RegexOptions.CultureInvariant);

    private readonly BackupSettings _settings;
    private readonly string _connectionString;

    public DatabaseBackup(BackupSettings settings, string connectionString)
    {
        _settings = settings;
        _connectionString = connectionString;
    }

    public BackupSettings Settings => _settings;

    /// <summary>备份目录的绝对路径。取用时才展开，构造时不去碰文件系统。</summary>
    public string DirectoryPath =>
        Path.IsPathRooted(_settings.Directory)
            ? _settings.Directory
            : Path.GetFullPath(_settings.Directory);

    /// <summary>文件名里的时间是定宽 UTC 的：字典序即时间序，裁剪只要按名字排。</summary>
    public static string FileNameFor(DateTimeOffset at) =>
        $"{Prefix}{at.ToUniversalTime():yyyyMMdd-HHmmss}{Suffix}";

    /// <summary>只认自己写过的文件名。裁剪不可逆，目录里的陌生文件一律不动。</summary>
    public static bool IsBackupName(string name) => BackupName.IsMatch(name);

    /// <summary>由新到旧。</summary>
    public IReadOnlyList<BackupFile> ListBackups()
    {
        if (!System.IO.Directory.Exists(DirectoryPath)) return [];
        return new DirectoryInfo(DirectoryPath)
            .EnumerateFiles("*" + Suffix)
            .Where(f => IsBackupName(f.Name))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupFile(f.Name, f.Length, new DateTimeOffset(f.CreationTimeUtc, TimeSpan.Zero)))
            .ToList();
    }

    /// <summary>
    /// 落一份备份：快照 → 校验 → 裁剪旧份。
    /// 校验失败会删掉坏文件并抛出，绝不会让"看着在、其实打不开"的备份冒充成功。
    /// </summary>
    public BackupFile Run()
    {
        var stamp = DateTimeOffset.UtcNow;
        var temp = RecipesDatabase.BackupToTempFile(_connectionString);
        try
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            var name = FileNameFor(stamp);
            var target = Path.Combine(DirectoryPath, name);
            // 同一秒内跑两次（手工点两下）会撞名：宁可报错也不覆盖上一份快照。
            if (File.Exists(target))
                throw new IOException($"{name} 已经存在：同一秒内不要连跑两次备份。");
            File.Copy(temp, target);
            try
            {
                Verify(target);
            }
            catch
            {
                File.Delete(target);
                throw;
            }

            PruneOldBackups();
            var info = new FileInfo(target);
            return new BackupFile(name, info.Length, stamp.ToUniversalTime());
        }
        finally
        {
            TryDelete(temp);
            // 快照会把 WAL 收敛进目标库，但源库的 -wal/-shm 仍在用；这里不碰它们。
        }
    }

    /// <summary>
    /// 删除超出保留份数的旧备份，返回删掉的文件名。
    ///
    /// <c>Keep &lt;= 0</c> 一律不删：把保留份数配成 0 的手误，代价不该是"历史备份全没"。
    /// 只按 <see cref="IsBackupName"/> 命中自己命名的文件，别的（包括人工放的 .bak、别的库）不动。
    /// </summary>
    public IReadOnlyList<string> PruneOldBackups()
    {
        if (_settings.Keep <= 0) return [];
        var victims = ListBackups().Skip(_settings.Keep).Select(f => f.Name).ToList();
        foreach (var name in victims)
            TryDelete(Path.Combine(DirectoryPath, name));
        return victims;
    }

    /// <summary>下一次该跑的 UTC 时刻，严格取未来：调度线程可能差几毫秒醒来，返回"今天"会变成空转。</summary>
    public DateTimeOffset NextRunAt(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var today = new DateTimeOffset(utc.Date, TimeSpan.Zero).Add(_settings.AtUtc.ToTimeSpan());
        return today > utc ? today : today.AddDays(1);
    }

    /// <summary>
    /// 打开副本跑 <c>PRAGMA integrity_check</c>，再数一张关键表的行数，最后把副本退回 rollback 日志模式。
    /// 备份的验收标准是"能恢复"，不是"文件在"——所以校验不能省。
    ///
    /// 退回 DELETE 是必须的而不是收拾场面：源库跑在 WAL 下，副本也是 WAL 库，
    /// 只要连过一次就会在旁边留下 <c>-wal</c> / <c>-shm</c>。一份"备份"如果是三个文件，
    /// 拷走单个 .db 就等于拷走一个可能读不到最新提交的半成品；而 rollback 模式的 .db 是自包含的单个文件。
    /// 注意这里不能用 Mode=ReadOnly：只读连接收尾时做不了 checkpoint，sidecar 会一直留着。
    /// </summary>
    private static void Verify(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        var result = check.ExecuteScalar() as string;
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"备份校验未通过：integrity_check 返回 {result ?? "空"}。");

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM production_batches";
        count.ExecuteScalar();

        using var demote = connection.CreateCommand();
        demote.CommandText = "PRAGMA journal_mode=DELETE";
        demote.ExecuteNonQuery();
        connection.Close();

        // 极端情况下（副本原本就带着上次的 sidecar）再清一遍，确保目录里一份备份就是一个文件。
        foreach (var sidecar in new[] { path + "-wal", path + "-shm" })
            TryDelete(sidecar);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 删不掉留给下一次裁剪：备份目录里多留一份，永远比少留一份好。
        }
    }
}
