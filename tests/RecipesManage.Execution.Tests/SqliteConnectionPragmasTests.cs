using Microsoft.EntityFrameworkCore;
using RecipesManage.Infrastructure.Persistence;
using Xunit;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 连接级 busy_timeout：新开的 SQLite 连接默认是 0，别的连接正在写时立刻报 "database is locked"。
/// 压力下这会把一个所有工步都已完成的批次置成 Faulted（见 SchedulerIntentAndAbortTests 的并行确认用例）。
/// </summary>
public sealed class SqliteConnectionPragmasTests
{
    private static async Task<long> ReadBusyTimeoutAsync(DbContextOptions<AppDbContext> options)
    {
        await using var db = new AppDbContext(options);
        // 必须经 EF 打开：直接 connection.OpenAsync() 绕过连接拦截器，测的就不是生产路径了。
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ConnectionsFromTheProductionConfiguration_WaitForLocksInsteadOfFailingAtOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"brmes-pragma-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>();
            RecipesDatabase.Apply(options, $"Data Source={path}");

            Assert.Equal(SqliteConnectionPragmas.DefaultBusyTimeoutMs, await ReadBusyTimeoutAsync(options.Options));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { /* temp db */ }
        }
    }

    [Fact]
    public async Task APlainConnection_HasNoBusyTimeout_WhichIsWhyTheInterceptorExists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"brmes-pragma-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;

            Assert.Equal(0, await ReadBusyTimeoutAsync(options));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { /* temp db */ }
        }
    }
}
