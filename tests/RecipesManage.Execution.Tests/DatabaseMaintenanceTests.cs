using Microsoft.Data.Sqlite;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// SQLite 维护作业的行为。重点不是"能不能跑"，而是<strong>什么时候不该跑</strong>：
/// VACUUM 会重写整个库文件，判断错了的代价是"现场机器在批次运行中被卡住"。
/// </summary>
public sealed class DatabaseMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"brmes-maint-{Guid.NewGuid():N}");

    public DatabaseMaintenanceTests() => Directory.CreateDirectory(_root);

    private string PathOf(string name) => Path.Combine(_root, name + ".db");

    /// <summary>
    /// 建一个有真实空闲页的库：灌一批行再删掉尾部。
    /// 必须<strong>连续删</strong>——隔行删除只让每页变半空，SQLite 只在整页空掉时才回收页，
    /// 那样 freelist 仍是 0，测不出 VACUUM 的作用（第一版就栽在这里）。
    /// </summary>
    private string BuildFragmentedDb(string name, int rows, int keep)
    {
        var path = PathOf(name);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, blob TEXT)";
            create.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO t(blob) VALUES (@v)";
            var value = insert.Parameters.Add("@v", SqliteType.Text);
            for (var i = 0; i < rows; i++)
            {
                value.Value = new string('x', 200);
                insert.ExecuteNonQuery();
            }
        }
        using (var del = connection.CreateCommand())
        {
            del.CommandText = "DELETE FROM t WHERE id > @keep";
            del.Parameters.AddWithValue("@keep", keep);
            del.ExecuteNonQuery();
        }
        return path;
    }

    private static DatabaseMaintenance Maintenance(string connectionString, MaintenanceSettings? settings = null) =>
        new(connectionString, settings ?? new MaintenanceSettings());

    [Fact]
    public void OptimizeRunsEvenWhenVacuumIsRefused()
    {
        var path = BuildFragmentedDb("opt", 400, 200);
        var result = Maintenance($"Data Source={path}", new MaintenanceSettings { MinFreeMegabytes = 0 })
            .Run(idleForVacuum: false);

        // 批次在跑 → 绝不重写文件，但 PRAGMA optimize 是廉价的、不改文件的，照跑。
        Assert.True(result.Optimized);
        Assert.False(result.Vacuumed);
        Assert.Contains("批次", result.SkippedReason);
        Assert.Equal(new FileInfo(path).Length, result.BytesBefore);
        // 没读 pragma 就别在审计行里写"空闲 0 / 0 页"——那会被读成"库很健康"。
        Assert.DoesNotContain("空闲", result.Describe());
    }

    [Fact]
    public void VacuumOnlyWhenIdleAndItActuallyShrinksTheFile()
    {
        var path = BuildFragmentedDb("vac", 2_000, 1_000);
        var before = new FileInfo(path).Length;

        var result = Maintenance($"Data Source={path}", new MaintenanceSettings { MinFreeMegabytes = 0 })
            .Run(idleForVacuum: true);

        Assert.True(result.Vacuumed, result.SkippedReason ?? "");
        Assert.True(result.FreePagesBefore > 0);
        Assert.True(result.ReclaimedBytes > 0, $"文件没缩：{before} → {new FileInfo(path).Length}");
        Assert.True(new FileInfo(path).Length < before);
        // 回收后数据仍在：VACUUM 是重写不是清空。
        using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        check.Open();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM t";
        Assert.Equal(1_000L, Convert.ToInt64(count.ExecuteScalar()));
    }

    [Fact]
    public void VacuumSwitchOffNeverTouchesTheFile()
    {
        var path = BuildFragmentedDb("off", 2_000, 1_000);
        var before = new FileInfo(path).Length;

        var result = Maintenance($"Data Source={path}", new MaintenanceSettings
        {
            Vacuum = false,
            MinFreeMegabytes = 0
        }).Run(idleForVacuum: true);

        Assert.False(result.Vacuumed);
        Assert.Equal(before, new FileInfo(path).Length);
    }

    [Fact]
    public void TinyFreeSpaceIsNotWorthRewritingTheWholeDatabase()
    {
        // 绝对量门槛挡住"10 MB 的库也去忙一场"：占比够也不算。
        Assert.False(DatabaseMaintenance.WorthVacuuming(
            freePages: 100, totalPages: 100, pageSize: 4096, new MaintenanceSettings { MinFreeMegabytes = 64 }));
        // 占比不够也不算，哪怕空闲量很大（1 GB 空闲 / 20 GB 总量 = 5%）。
        Assert.False(DatabaseMaintenance.WorthVacuuming(
            freePages: 262_144, totalPages: 5_242_880, pageSize: 4096, new MaintenanceSettings { MinFreeMegabytes = 64 }));
        Assert.True(DatabaseMaintenance.WorthVacuuming(
            freePages: 262_144, totalPages: 524_288, pageSize: 4096, new MaintenanceSettings { MinFreeMegabytes = 64 }));
        // 空库/未知页数一律不动手。
        Assert.False(DatabaseMaintenance.WorthVacuuming(0, 0, 0, new MaintenanceSettings { MinFreeMegabytes = 0 }));
    }

    [Fact]
    public void MemoryDatabasesAreSkippedInsteadOfThrowing()
    {
        var result = Maintenance("Data Source=:memory:").Run(idleForVacuum: true);
        Assert.False(result.Vacuumed);
        Assert.False(result.Optimized);
        Assert.Contains("内存库", result.SkippedReason);
    }

    [Fact]
    public void DescribeSaysWhatHappenedInOneLine()
    {
        var path = BuildFragmentedDb("desc", 2_000, 1_000);
        var result = Maintenance($"Data Source={path}", new MaintenanceSettings { MinFreeMegabytes = 0 })
            .Run(idleForVacuum: true);

        // 这行要直接落进审计履历，所以必须自解释：做了什么、回收了多少、或者为什么没做。
        Assert.Contains("PRAGMA optimize", result.Describe());
        Assert.Contains("VACUUM 回收", result.Describe());
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录留在 %TEMP%，比让测试变红合适。
        }
    }
}
