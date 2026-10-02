using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Infrastructure.Persistence;

namespace RecipesManage.Api.Controllers;

/// <summary>
/// 数据库备份的运维面：看现状、手工补一份。
///
/// 只有管理员能读——备份文件里是全套口令哈希与审计履历，列个文件名也是信息。
/// 与 <c>POST /api/users/sqlite-backup</c> 的分工：那一个是"把库取走"（要电子签名、把字节交给浏览器），
/// 这两个接口管的是留在服务器上的那份日常快照。
/// </summary>
[Authorize(Policy = AuthorizationPolicies.Admin)]
[ApiController]
[Route("api/system")]
public sealed class SystemController(
    DatabaseBackup backup,
    DailyBackupHostedService runner,
    ICurrentUser user) : ControllerBase
{
    [HttpGet("backups")]
    public BackupStatusDto Status() => Build(DateTimeOffset.UtcNow);

    /// <summary>手工落一份：定时任务没跑起来（或者刚换过磁盘）时，管理员要能当场补一份并看到结果。</summary>
    [HttpPost("backups")]
    public async Task<ActionResult<BackupFileDto>> RunNow(CancellationToken ct)
    {
        try
        {
            var file = await runner.RunOnceAsync(ct, user.UserName, user.UserId);
            return Ok(Map(file));
        }
        catch (Exception e) when (e is InvalidOperationException or FileNotFoundException or IOException)
        {
            // 内存库、磁盘满、同一秒撞名都在这里；给可读的 400，不要把堆栈抛给界面。
            return BadRequest(new { code = "BACKUP_FAILED", message = e.Message });
        }
    }

    /// <summary>
    /// 手工跑一轮维护（PRAGMA optimize + 条件满足时 VACUUM）。
    /// 现场磁盘吃紧时不用等下一个计划时刻；但批次在跑时它只会做 optimize 并说明为什么没 VACUUM，
    /// 这个"拒绝"是正常结果，不是失败。
    /// </summary>
    [HttpPost("maintenance")]
    public async Task<ActionResult<MaintenanceResultDto>> RunMaintenance(CancellationToken ct)
    {
        try
        {
            var result = await runner.RunMaintenanceAsync(user.UserName, user.UserId, ct);
            return Ok(new MaintenanceResultDto(
                result.Optimized, result.Vacuumed, result.BytesBefore, result.BytesAfter,
                result.ReclaimedBytes, result.SkippedReason, result.Describe()));
        }
        catch (Exception e) when (e is InvalidOperationException or FileNotFoundException or IOException)
        {
            return BadRequest(new { code = "MAINTENANCE_FAILED", message = e.Message });
        }
    }

    private BackupStatusDto Build(DateTimeOffset now)
    {
        var settings = backup.Settings;
        return new BackupStatusDto(
            backup.DirectoryPath,
            settings.Enabled,
            settings.Keep,
            settings.AtUtc.ToString("HH:mm"),
            backup.NextRunAt(now),
            backup.ListBackups().Select(Map).ToList(),
            runner.LastRunAtUtc,
            runner.LastFile,
            runner.LastError);
    }

    private static BackupFileDto Map(BackupFile file) => new(file.Name, file.Bytes, file.CreatedAt);
}
