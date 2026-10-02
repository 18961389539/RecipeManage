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

    /// <summary>
    /// 升级前快照留几份。默认 3：它们只是同一天那份数据的另一份拷贝，不是记录本身，
    /// 每次升级都留一份、永久累积没有意义；<c>0</c> 表示全留。
    /// </summary>
    public int KeepPreMigration { get; init; } = 3;

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

    /// <summary>由新到旧。传目录就只看那个目录（升级前快照在子目录里，与每日快照分开裁）。</summary>
    public IReadOnlyList<BackupFile> ListBackups(string? directory = null)
    {
        var dir = directory ?? DirectoryPath;
        if (!System.IO.Directory.Exists(dir)) return [];
        return new DirectoryInfo(dir)
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
    public BackupFile Run() => SnapshotInto(DirectoryPath, _settings.Keep);

    /// <summary>
    /// 升级（迁移）之前的那份快照，落在 <c>backups/pre-migration/</c> 里，与每日快照分开。
    ///
    /// 为什么要单独一个目录：每日快照按 Keep 裁剪，升级前那份要是混在一起，
    /// 第 8 次升级就会把第 1 次的退路裁掉；而退路的价值恰恰只在"升坏了"的那一刻。
    /// 为什么不递归进 <see cref="ListBackups"/>：目录不递归，所以界面的备份列表里看不到它们，
    /// 也就不会被"下载/裁剪"这类日常动作误伤。
    /// </summary>
    public BackupFile SnapshotBeforeMigration() =>
        SnapshotInto(PreMigrationDirectory, _settings.KeepPreMigration);

    /// <summary>升级前快照目录。在备份目录之下，跟着它一起被机器自己的复制计划带走。</summary>
    public string PreMigrationDirectory => Path.Combine(DirectoryPath, "pre-migration");

    private BackupFile SnapshotInto(string directory, int keep)
    {
        var stamp = DateTimeOffset.UtcNow;
        var temp = RecipesDatabase.BackupToTempFile(_connectionString);
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var name = FileNameFor(stamp);
            var target = Path.Combine(directory, name);
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

            if (keep > 0)
                foreach (var victim in ListBackups(directory).Skip(keep).Select(f => f.Name))
                    TryDelete(Path.Combine(directory, victim));

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

    /// <summary>最近一个**已经过去**的计划时刻。每日一班，所以它就是 <see cref="NextRunAt"/> 的前一天。</summary>
    public DateTimeOffset LastScheduledAt(DateTimeOffset now) => NextRunAt(now).AddDays(-1);

    /// <summary>
    /// 目录里是否已有落在某一天的快照。只看文件名里的日期，不看文件系统时间戳：
    /// 快照拷进拷出、U 盘倒腾都会改 mtime，而文件名是 <see cref="FileNameFor"/> 写死的定宽 UTC。
    /// </summary>
    public bool HasBackupOn(DateOnly day) =>
        ListBackups().Any(f =>
            f.Name.Length >= Prefix.Length + 8
            && DateOnly.TryParseExact(f.Name.AsSpan(Prefix.Length, 8), "yyyyMMdd", out var found)
            && found == day);

    /// <summary>
    /// 开机要不要补跑：最近一个已过去的计划时刻那天没有快照就要。
    ///
    /// 为什么必须有：现场机器不是 7×24 的，晚上 02:15 大概率是关着的。只等"下一次"的话，
    /// 天天在计划时刻关机的机器一份备份都不会产生——备份策略看起来在跑，其实从没兑现过。
    /// 补跑只看"最近那一班"，缺几天也只补一份：这是每日快照，不是要补齐历史。
    /// </summary>
    public bool NeedsCatchUp(DateTimeOffset now) =>
        !HasBackupOn(DateOnly.FromDateTime(LastScheduledAt(now).UtcDateTime));


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
