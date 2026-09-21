using System.Net.Sockets;
using System.Text.RegularExpressions;
using Npgsql;

namespace RecipesManage.Infrastructure.Persistence;

/// <summary>
/// 无 Docker 的 PostgreSQL 运行时：优先使用环境变量 BRMES_POSTGRES；
/// 若设置 BRMES_EMBEDDED_POSTGRES=1 则启动本机嵌入式 PostgreSQL 15（MysticMind.PostgresEmbed）。
/// 默认 API 仍走 SQLite。
/// </summary>
public sealed class EmbeddedPostgresRuntime : IAsyncDisposable
{
    public const string EnvConnection = "BRMES_POSTGRES";
    public const string EnvEmbed = "BRMES_EMBEDDED_POSTGRES";
    public const string DefaultAppDatabase = "brmes";
    public const int DefaultPort = 55432;

    private static readonly Guid InstanceId = Guid.Parse("6b726d65-0000-4000-8000-7067656d6264");
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IAsyncDisposable? _server;

    private EmbeddedPostgresRuntime(string connectionString, IAsyncDisposable? server, string adminConnectionString)
    {
        ConnectionString = connectionString;
        AdminConnectionString = adminConnectionString;
        _server = server;
    }

    public string ConnectionString { get; }
    public string AdminConnectionString { get; }

    public static bool ShouldStart()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvConnection)))
            return false;
        var flag = Environment.GetEnvironmentVariable(EnvEmbed);
        return flag is "1" or "true" or "TRUE" or "yes" or "YES";
    }

    public static async Task<EmbeddedPostgresRuntime> StartAsync()
    {
        var existing = Environment.GetEnvironmentVariable(EnvConnection);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            var cs = existing.Trim();
            return new EmbeddedPostgresRuntime(cs, null, cs);
        }

        await Gate.WaitAsync();
        try
        {
            var admin = AdminCs(DefaultPort);
            if (await WaitForReadyAsync(admin, TimeSpan.FromSeconds(3)))
                return new EmbeddedPostgresRuntime(admin, null, admin);

            await WaitUntilPortFreeAsync(DefaultPort, TimeSpan.FromSeconds(15));

            var dir = ResolveDataDirectory();
            Directory.CreateDirectory(dir);
            var server = new MysticMind.PostgresEmbed.PgServer(
                "15.3.0",
                dbDir: dir,
                instanceId: InstanceId,
                port: DefaultPort,
                addLocalUserAccessPermission: true,
                clearInstanceDirOnStop: false,
                startupWaitTime: 120_000);
            await server.StartAsync();
            admin = AdminCs(server.PgPort);
            if (!await WaitForReadyAsync(admin, TimeSpan.FromSeconds(45)))
                throw new InvalidOperationException("嵌入式 PostgreSQL 端口已监听，但尚未接受 SQL 连接。");
            return new EmbeddedPostgresRuntime(admin, server, admin);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 稳定目录优先：%LOCALAPPDATA%\BRMES\pg-embed；已有临时目录数据则继续用，避免冷启动丢库。
    /// BRMES_PG_DIR 可覆盖。
    /// </summary>
    public static string ResolveDataDirectory() =>
        ResolveDataDirectory(
            Environment.GetEnvironmentVariable("BRMES_PG_DIR"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BRMES", "pg-embed"),
            Path.Combine(Path.GetTempPath(), "brmes-pg-embed"));

    public static string ResolveDataDirectory(string? configured, string stable, string legacy)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();
        if (HasInstanceFiles(stable))
            return stable;
        if (HasInstanceFiles(legacy))
            return legacy;
        Directory.CreateDirectory(stable);
        return stable;
    }

    private static bool HasInstanceFiles(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();

    public async Task<string> EnsureDatabaseAsync(string name, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(name, "^[a-z][a-z0-9_]*$"))
            throw new ArgumentException("数据库名只能包含小写字母、数字与下划线。", nameof(name));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        PostgresException? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var conn = new NpgsqlConnection(AdminConnectionString);
                await conn.OpenAsync(ct);
                await using (var exists = conn.CreateCommand())
                {
                    exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @n";
                    exists.Parameters.AddWithValue("n", name);
                    if (await exists.ExecuteScalarAsync(ct) is not null)
                        return WithDatabase(name);
                }

                await using var create = conn.CreateCommand();
                create.CommandText = "CREATE DATABASE " + name;
                await create.ExecuteNonQueryAsync(ct);
                return WithDatabase(name);
            }
            catch (PostgresException ex) when (ex.SqlState is "57P03" or "57P01")
            {
                last = ex;
                await Task.Delay(400, ct);
            }
        }

        if (last is not null)
            throw last;
        throw new TimeoutException("嵌入式 PostgreSQL 在超时前未接受连接。");
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }

    private string WithDatabase(string name)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = name,
            Pooling = false
        };
        return builder.ConnectionString;
    }

    private static string AdminCs(int port) =>
        $"Host=127.0.0.1;Port={port};Username=postgres;Password=test;Database=postgres;Pooling=false;Timeout=5";

    private static async Task<bool> WaitForReadyAsync(string cs, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            if (await TryQueryAsync(cs))
                return true;
            await Task.Delay(200);
        }

        return false;
    }

    private static async Task<bool> TryQueryAsync(string cs)
    {
        try
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var ping = conn.CreateCommand();
            ping.CommandText = "SELECT 1";
            return Equals(1, await ping.ExecuteScalarAsync());
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitUntilPortFreeAsync(int port, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsPortOpen(port))
                return;
            await Task.Delay(250);
        }
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(TimeSpan.FromMilliseconds(200)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
