using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Identity;
using RecipesManage.Infrastructure.Persistence;

namespace RecipesManage.Execution;

/// <summary>
/// 每天定时落一份 SQLite 备份，并按保留份数裁剪旧份。
///
/// 为什么要有：整套系统只有一个 .db 文件——审计履历、控制配方快照、电子签名全在里面。
/// 磁盘坏了或者误删一批，记录就一起没了，而 GMP 侧要求这些记录留到留存期。
/// 过程样本一行都不删（批记录要引用），库只会越长越大，所以"留几份历史快照"比"定期清样本"更要紧。
///
/// 跑失败不会停掉宿主，也不会静默：错误进日志，同时写一条 <c>system.backup.failed</c> 审计行，
/// 这样管理员在操作审计里能看到"哪天开始没备份上"，而不是等到要恢复时才发现。
/// </summary>
public sealed class DailyBackupHostedService : BackgroundService
{
    /// <summary>审计行里的操作者。定时任务背后没有"人"，所以留空 UserId、用户名固定 system。</summary>
    public const string SystemActor = "system";

    private readonly DatabaseBackup _backup;
    private readonly DatabaseMaintenance _maintenance;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DailyBackupHostedService> _log;

    public DailyBackupHostedService(
        DatabaseBackup backup,
        DatabaseMaintenance maintenance,
        IServiceScopeFactory scopes,
        ILogger<DailyBackupHostedService> log)
    {
        _backup = backup;
        _maintenance = maintenance;
        _scopes = scopes;
        _log = log;
    }

    /// <summary>本进程内的最近一次结果。磁盘上有什么以 <see cref="DatabaseBackup.ListBackups"/> 为准。</summary>
    public DateTimeOffset? LastRunAtUtc { get; private set; }
    public string? LastFile { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// 跑一次并留痕。手工触发（管理员点"立即备份"）和定时触发走的是同一个方法，
    /// 两条路径的审计口径不会分叉；区别只在操作者：定时的没有"人"，记 system，
    /// 手工的必须能追到是谁让库多一份快照的。
    /// </summary>
    public async Task<BackupFile> RunOnceAsync(
        CancellationToken ct = default,
        string actor = SystemActor,
        Guid? actorId = null)
    {
        LastRunAtUtc = DateTimeOffset.UtcNow;
        try
        {
            // 复制与校验是同步文件 IO，放到线程池里做，别占着后台服务的调度线程。
            var file = await Task.Run(_backup.Run, ct);
            LastFile = file.Name;
            LastError = null;
            await RecordAsync(actor, actorId, "system.backup", file.Name,
                $"{file.Bytes:N0} B · 保留 {_backup.Settings.Keep} 份", ct);
            _log.LogInformation("数据库备份完成：{File}（{Bytes} 字节）", file.Name, file.Bytes);
            return file;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastError = e.Message;
            // 记失败要用独立的 ct：整个进程正在停的时候，这条审计行恰恰最有用。
            await RecordAsync(actor, actorId, "system.backup.failed", "", e.Message, CancellationToken.None);
            _log.LogError(e, "数据库备份失败");
            throw;
        }
    }

    private async Task RecordAsync(
        string actor, Guid? actorId, string action, string entityId, string detail, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.AuditLogs.Add(new AuditLog(actorId, actor, action, "System", entityId, detail));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e)
        {
            // 审计行写不进去不该让备份本身回滚：快照已经在磁盘上了，日志再单独报。
            _log.LogWarning(e, "备份审计行写入失败：{Action}", action);
        }
    }

    /// <summary>
    /// 跑一轮维护并留痕。定时与手工共用，区别只在审计行上的操作者。
    /// 不做异常吞没：手工触发时管理员要看到真实结果（包括"因为批次在跑所以没 VACUUM"）。
    /// </summary>
    public async Task<MaintenanceResult> RunMaintenanceAsync(
        string actor = SystemActor, Guid? actorId = null, CancellationToken ct = default)
    {
        var idle = await IsIdleAsync(ct);
        var result = await Task.Run(() => _maintenance.Run(idle), ct);
        await RecordAsync(actor, actorId, "system.maintenance", "", result.Describe(), ct);
        _log.LogInformation("数据库维护：{Detail}", result.Describe());
        return result;
    }

    /// <summary>
    /// 每天一轮 SQLite 维护，**排在当天备份之后**。
    ///
    /// 顺序不是讲究而是安全：VACUUM 要把整个库文件重写一遍，中途断电或磁盘写满时，
    /// 手里必须已经有一份当天刚验过的快照。所以备份抛异常时这里根本不会被调用。
    /// 维护本身失败也绝不能把已经成功的备份结果带坏 —— 只记日志与审计。
    /// </summary>
    private async Task MaintainAsync(CancellationToken ct)
    {
        if (!_maintenance.Settings.Enabled) return;
        try
        {
            await RunMaintenanceAsync(SystemActor, null, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "数据库维护本轮跳过（备份结果不受影响）");
        }
    }

    /// <summary>有没有批次在跑。VACUUM 期间引擎每秒两次的事务会被挡住，而那在现在的实现里会把批次置成故障。</summary>
    private async Task<bool> IsIdleAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        return !await db.Batches.AsNoTracking().AnyAsync(b =>
            b.Status == BatchStatus.Running || b.Status == BatchStatus.Queued
            || b.Status == BatchStatus.Held || b.Status == BatchStatus.Faulted, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_backup.Settings.Enabled)
        {
            _log.LogInformation("每日数据库备份已禁用（Backup:Enabled=false）");
            return;
        }

        _log.LogInformation(
            "每日数据库备份已启用：每天 {At} UTC 落到 {Dir}，保留 {Keep} 份；维护 {Maintenance}",
            _backup.Settings.AtUtc.ToString("HH:mm"), _backup.DirectoryPath, _backup.Settings.Keep,
            _maintenance.Settings.Enabled ? "随后执行" : "已禁用");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var delay = _backup.NextRunAt(now) - now;
            try
            {
                // 醒来可能差几毫秒：不足 1 秒就补睡 1 秒，避免空转。
                await Task.Delay(delay <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delay, stoppingToken);
                await RunOnceAsync(stoppingToken);
                await MaintainAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;      // 进程在停，正常退出
            }
            catch (Exception e)
            {
                // 单次失败不能拖停宿主；下一分钟重新排程。
                _log.LogError(e, "每日备份本轮失败");
            }
        }
    }
}
