using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 单实例互斥的行为契约。
///
/// 这条防线挡的是"两个引擎同时驱动同一台设备"，所以它的判据必须是**独占句柄**，
/// 不是锁文件存不存在（文件在进程被硬杀后仍然在，靠文件判断会永久拒绝启动）。
/// </summary>
public sealed class SingleInstanceLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"brmes-lock-test-{Guid.NewGuid():N}");

    public SingleInstanceLockTests() => Directory.CreateDirectory(_root);

    private string Connection(string name) => $"Data Source={Path.Combine(_root, name + ".db")}";

    [Fact]
    public void SecondAcquireOnTheSameDatabaseIsRefusedWhileTheFirstIsHeld()
    {
        var held = SingleInstanceLock.TryAcquire(Connection("recipes"), out var skipped);
        Assert.False(skipped);
        Assert.NotNull(held);

        Assert.Null(SingleInstanceLock.TryAcquire(Connection("recipes")));

        held!.Dispose();
        // 释放（含进程退出时由操作系统回收句柄）之后必须能重新拿到，否则崩一次就再也起不来。
        Assert.NotNull(SingleInstanceLock.TryAcquire(Connection("recipes")));
    }

    [Fact]
    public void DifferentDatabasesDoNotBlockEachOther()
    {
        // 同一台机器上两份 checkout（各自的 App_Data）要能并行跑，锁的身份是"这个库"而不是"这台机器"。
        using var a = SingleInstanceLock.TryAcquire(Connection("recipes"));
        using var b = SingleInstanceLock.TryAcquire(Connection("recipes.design"));
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public void InMemoryDatabasesSkipLockingInsteadOfRefusingToStart()
    {
        // 设计期工具与部分测试用 :memory:，没有可锁的文件；这里必须放行，不能变成"起不来"。
        Assert.Null(SingleInstanceLock.TryAcquire("Data Source=:memory:", out var skipped));
        Assert.True(skipped);
        Assert.Null(SingleInstanceLock.TryAcquire("Data Source=Mode=Memory;Cache=Shared", out skipped));
        Assert.True(skipped);
    }

    [Fact]
    public void RefusalStillNamesTheCurrentHolder()
    {
        using var held = SingleInstanceLock.TryAcquire(Connection("recipes"), out _);
        Assert.NotNull(held);

        var owner = SingleInstanceLock.DescribeOwner(Connection("recipes"));

        // 报错要能回答"那是谁"，否则现场只会看到"起不来"四个字。
        Assert.Contains($"pid={Environment.ProcessId}", owner, StringComparison.Ordinal);
        Assert.Contains("machine=", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void LockFileStaysInTheSameDirectoryAsTheDatabaseAndSurvivesDispose()
    {
        var held = SingleInstanceLock.TryAcquire(Connection("recipes"), out _);
        Assert.NotNull(held);
        Assert.Equal(Path.Combine(_root, "recipes.db.lock"), held.Path);

        held.Dispose();
        // 故意不删：删掉会开一个窗口——A 正在独占打开、B 看文件不存在就以为没人在跑。
        Assert.True(File.Exists(held.Path));
    }

    [Fact]
    public void MissingDirectoryIsCreatedRatherThanThrowing()
    {
        var connection = $"Data Source={Path.Combine(_root, "nested", "deeper", "recipes.db")}";
        Assert.False(Directory.Exists(Path.Combine(_root, "nested")));

        using var held = SingleInstanceLock.TryAcquire(connection, out var skipped);

        Assert.False(skipped);
        Assert.NotNull(held);
        Assert.True(Directory.Exists(Path.Combine(_root, "nested", "deeper")));
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
