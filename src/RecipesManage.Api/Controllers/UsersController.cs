using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Identity;
using RecipesManage.Infrastructure.Persistence;

namespace RecipesManage.Api.Controllers;

[Authorize]
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

[Authorize]
[ApiController]
[Route("api/admin")]
public sealed class AdminController(IConfiguration config, ICurrentUser user) : ControllerBase
{
    [HttpGet("sqlite-backup")]
    public IActionResult SqliteBackup()
    {
        if (user.Role is not UserRole.Admin)
            return Forbid();

        var path = RecipesDatabase.TryGetSqliteFilePath(
            config.GetConnectionString("PostgreSQL"),
            config.GetConnectionString("Sqlite"));
        if (path is null)
            return BadRequest(new { code = "NOT_SQLITE", message = "当前不是 SQLite 文件库。PostgreSQL 请使用 pg_dump。" });
        if (!System.IO.File.Exists(path))
            return NotFound(new { code = "NO_DB", message = "找不到 SQLite 文件。" });

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        var name = $"brmes-{DateTime.UtcNow:yyyyMMddHHmmss}.db";
        return File(ms.ToArray(), "application/vnd.sqlite3", name);
    }
}
