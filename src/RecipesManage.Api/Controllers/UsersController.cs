using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Identity;
using RecipesManage.Infrastructure.Persistence;

namespace RecipesManage.Api.Controllers;

[Authorize(Policy = AuthorizationPolicies.Admin)]
[ApiController]
[Route("api/users")]
public sealed class UsersController(AuthService auth) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<UserDto>> List(CancellationToken ct) => auth.ListUsersAsync(ct);

    [HttpPost]
    public Task<UserDto> Create(CreateUserRequest request, CancellationToken ct) =>
        auth.CreateUserAsync(request, ct);

    [HttpPut("{id:guid}")]
    public Task<UserDto> Update(Guid id, UpdateUserRequest request, CancellationToken ct) =>
        auth.UpdateUserAsync(id, request, ct);
}

[Authorize(Policy = AuthorizationPolicies.Admin)]
[ApiController]
[Route("api/admin")]
public sealed class AdminController(IConfiguration config, AuthService auth, ILogger<AdminController> log) : ControllerBase
{
    /// <summary>
    /// 整库备份。要求 Admin 策略 + 电子签名 + 审计（见 <see cref="AuthService.RequireBackupEsignAsync"/>）。
    ///
    /// 两处改动过：
    /// - GET → POST：GET 会被代理/浏览器缓存与记录，也容易被一个 &lt;img&gt; 触发；
    ///   而且密码不该出现在 URL 里。
    /// - 直接拷文件 → SQLite Backup API：库在 WAL 模式下，裸复制会得到"主库 + 半截 WAL"的撕裂快照。
    /// </summary>
    [HttpPost("sqlite-backup")]
    public async Task<IActionResult> SqliteBackup([FromBody] EsignActionRequest request, CancellationToken ct)
    {
        var name = $"brmes-{DateTime.UtcNow:yyyyMMddHHmmss}.db";
        await auth.RequireBackupEsignAsync(request.Password, name, ct);

        string path;
        try
        {
            path = RecipesDatabase.BackupToTempFile(config.GetConnectionString("Sqlite"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            return BadRequest(new { code = "NOT_SQLITE", message = ex.Message });
        }

        try
        {
            return File(await System.IO.File.ReadAllBytesAsync(path, ct), "application/vnd.sqlite3", name);
        }
        finally
        {
            // 临时快照里是全套口令哈希。删不掉必须喊出来——静默吞掉就等于把整库留在 %TEMP% 里。
            try
            {
                System.IO.File.Delete(path);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "整库备份临时文件未删除，含口令哈希，请手工清理：{Path}", path);
            }
        }
    }
}
