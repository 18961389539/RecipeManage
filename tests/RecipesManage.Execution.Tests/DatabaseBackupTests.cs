using Microsoft.EntityFrameworkCore;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 每日备份的三件核心事实：命名可辨认、裁剪只删自己写的、产物真能打开。
///
/// 裁剪是这套代码里唯一**不可逆**的动作，所以它的边界条件（陌生文件名、保留份数配成 0）
/// 必须钉住——线上传错一个配置就把历史快照全删了，这种事只会发生一次。
/// </summary>
public sealed class DatabaseBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"brmes-backup-test-{Guid.NewGuid():N}");

    public DatabaseBackupTests() => Directory.CreateDirectory(_root);

    private string BackupsDir(string leaf = "backups") => Path.Combine(_root, leaf);

    private static DatabaseBackup NewBackup(string connectionString, string directory, int keep = 7, TimeOnly? atUtc = null) =>
        new(new BackupSettings { Keep = keep, Directory = directory, AtUtc = atUtc ?? new TimeOnly(2, 15) }, connectionString);

    private string SeedSourceDb()
    {
        var path = Path.Combine(_root, "recipes.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}").Options;
        using var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        db.AuditLogs.Add(new RecipesManage.Domain.Identity.AuditLog(null, "tester", "audit.test", "Test", "1", "详情"));
        db.SaveChanges();
        return path;
    }

    [Fact]
    public void NamesAreFixedWidthUtcAndOnlyItsOwnFilesAreRecognised()
    {
        var name = DatabaseBackup.FileNameFor(new DateTimeOffset(2026, 9, 24, 2, 15, 3, TimeSpan.Zero));
        Assert.Equal("brmes-20260924-021503.db", name);
        // 带时区偏移的时刻要先归一到 UTC，否则字典序不再是时间序。
        Assert.Equal(name, DatabaseBackup.FileNameFor(
            new DateTimeOffset(2026, 9, 23, 22, 15, 3, TimeSpan.FromHours(-4))));

        Assert.True(DatabaseBackup.IsBackupName(name));
        // 「下载 SQLite」那份是给浏览器的人工命名（无短横），不能被当成自动备份裁掉。
        Assert.False(DatabaseBackup.IsBackupName("brmes-20260924021503.db"));
        Assert.False(DatabaseBackup.IsBackupName("recipes.db"));
        Assert.False(DatabaseBackup.IsBackupName("brmes-20260924-021503.db.bak"));
        Assert.False(DatabaseBackup.IsBackupName("BRMES-20260924-021503.DB"));
    }

    [Fact]
    public void PruneKeepsTheNewestCopiesAndNeverTouchesForeignFiles()
    {
        var dir = BackupsDir();
        Directory.CreateDirectory(dir);
        for (var day = 1; day <= 5; day++)
            File.WriteAllText(Path.Combine(dir, $"brmes-202609{day:D2}-021503.db"), new string('x', day));
        File.WriteAllText(Path.Combine(dir, "manual-copy.db"), "keep");           // 人工放的
        File.WriteAllText(Path.Combine(dir, "brmes-not-a-timestamp.db"), "keep"); // 撞前缀但不是它写的
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "keep");

        var deleted = NewBackup("unused", dir, keep: 2).PruneOldBackups();

        Assert.Equal(
            ["brmes-20260901-021503.db", "brmes-20260902-021503.db", "brmes-20260903-021503.db"],
            deleted.OrderBy(x => x, StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(dir, "brmes-20260905-021503.db")));
        Assert.True(File.Exists(Path.Combine(dir, "brmes-20260904-021503.db")));
        Assert.True(File.Exists(Path.Combine(dir, "manual-copy.db")));
        Assert.True(File.Exists(Path.Combine(dir, "brmes-not-a-timestamp.db")));
        Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));
    }

    [Fact]
    public void ZeroOrNegativeKeepDisablesPruningInsteadOfEmptyingTheDirectory()
    {
        var dir = BackupsDir();
        Directory.CreateDirectory(dir);
        for (var day = 1; day <= 3; day++)
            File.WriteAllText(Path.Combine(dir, $"brmes-202609{day:D2}-021503.db"), "x");

        // 把保留份数配成 0 的手误，代价不该是"历史备份全没"。
        Assert.Empty(NewBackup("unused", dir, keep: 0).PruneOldBackups());
        Assert.Empty(NewBackup("unused", dir, keep: -5).PruneOldBackups());
        Assert.Equal(3, new DirectoryInfo(dir).GetFiles().Length);
    }

    [Fact]
    public void RunWritesARestorableSnapshotAndListsItNewestFirst()
    {
        var source = SeedSourceDb();
        var dir = BackupsDir();
        var backup = NewBackup($"Data Source={source}", dir, keep: 7);

        var first = backup.Run();
        var target = Path.Combine(dir, first.Name);
        Assert.Equal(DatabaseBackup.FileNameFor(first.CreatedAt), first.Name);
        Assert.True(File.Exists(target));
        Assert.True(first.Bytes > 0);

        Assert.Single(backup.ListBackups());
        // 一份备份必须是一个自包含的文件：留 -wal/-shm 意味着"拷走 .db"会拷走一个读不全的半成品。
        Assert.False(File.Exists(target + "-wal"));
        Assert.False(File.Exists(target + "-shm"));
        using (var probe = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={target};Pooling=False"))
        {
            probe.Open();
            using var mode = probe.CreateCommand();
            mode.CommandText = "PRAGMA journal_mode";
            Assert.NotEqual("wal", mode.ExecuteScalar() as string, StringComparer.OrdinalIgnoreCase);
        }
        // 备份要真的能打开并读回数据，否则"文件在"没有任何意义。
        using var restored = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={target}").Options);
        Assert.Equal(1, restored.AuditLogs.Count());
    }

    [Fact]
    public void NextRunIsAlwaysInTheFutureAndRollsOverAfterTodaySlot()
    {
        var backup = NewBackup("unused", BackupsDir(), atUtc: new TimeOnly(2, 15));
        var before = new DateTimeOffset(2026, 9, 24, 1, 0, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 9, 24, 2, 15, 1, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 9, 24, 2, 15, 0, TimeSpan.Zero), backup.NextRunAt(before));
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 2, 15, 0, TimeSpan.Zero), backup.NextRunAt(after));
        // 本地时区进来也要按 UTC 判：差 8 小时会让备份整天不跑。
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 2, 15, 0, TimeSpan.Zero),
            backup.NextRunAt(new DateTimeOffset(2026, 9, 23, 20, 0, 0, TimeSpan.FromHours(-6))));
        Assert.True(backup.NextRunAt(after) > after);
    }

    [Fact]
    public void CatchUpFiresForTheSlotThatAlreadyPassedAndNotAgainAfterwards()
    {
        var dir = BackupsDir();
        Directory.CreateDirectory(dir);
        var backup = NewBackup("unused", dir, atUtc: new TimeOnly(2, 15));
        var morning = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);

        // 机器 02:15 正关着 → 开机时这一班是欠的。
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 2, 15, 0, TimeSpan.Zero), backup.LastScheduledAt(morning));
        Assert.True(backup.NeedsCatchUp(morning));

        // 补上了（哪怕补跑落在 09:30，或管理员手工点过一次）就不该再来第二份。
        File.WriteAllText(Path.Combine(dir, "brmes-20260925-093012.db"), "x");
        Assert.False(backup.NeedsCatchUp(morning));

        // 换到第二天：昨天的快照不能顶替今天这一班，缺几天也只补一份。
        Assert.True(backup.NeedsCatchUp(new DateTimeOffset(2026, 9, 26, 23, 0, 0, TimeSpan.Zero)));

        // 凌晨 01:00 开机：今天那一班还没到，欠的是昨天那份，而它已经在 → 不该在 01:00 又补一份。
        Assert.False(backup.NeedsCatchUp(new DateTimeOffset(2026, 9, 26, 1, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CatchUpJudgesByTheDateInTheNameNeverByFilesystemTimestamps()
    {
        var dir = BackupsDir();
        Directory.CreateDirectory(dir);
        var backup = NewBackup("unused", dir, atUtc: new TimeOnly(2, 15));
        var day = new DateOnly(2026, 9, 25);
        var now = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);

        // 从 U 盘拷回来、被同步盘倒过手，mtime 都会变；文件名里的定宽 UTC 才是凭据。
        var copied = Path.Combine(dir, "brmes-20260925-021503.db");
        File.WriteAllText(copied, "x");
        File.SetLastWriteTimeUtc(copied, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(backup.HasBackupOn(day));

        // 陌生文件名一律不算，否则谁都能在备份目录里"伪造"出一份快照、让补跑静默跳过。
        File.Delete(copied);
        File.WriteAllText(Path.Combine(dir, "brmes-20260925-021503.db.bak"), "x");
        File.WriteAllText(Path.Combine(dir, "recipes-20260925.db"), "x");
        Assert.False(backup.HasBackupOn(day));
        Assert.True(backup.NeedsCatchUp(now));
    }

    [Fact]
    public void MissingSourceFileFailsLoudInsteadOfWritingAnEmptyBackup()
    {
        var backup = NewBackup($"Data Source={Path.Combine(_root, "nope.db")}", BackupsDir());
        Assert.ThrowsAny<Exception>(() => backup.Run());
        Assert.Empty(backup.ListBackups());
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录留在 %TEMP% 里，比让测试变红合适。
        }
    }
}
