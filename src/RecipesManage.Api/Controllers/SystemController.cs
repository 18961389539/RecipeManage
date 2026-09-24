using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Execution;
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
[Route("api/system/backups")]
public sealed class SystemController(
    DatabaseBackup backup,
    DailyBackupHostedService runner,
    ICurrentUser user) : ControllerBase
{
    [HttpGet]
    public BackupStatusDto Status() => Build(DateTimeOffset.UtcNow);

    /// <summary>手工落一份：定时任务没跑起来（或者刚换过磁盘）时，管理员要能当场补一份并看到结果。</summary>
    [HttpPost]
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
