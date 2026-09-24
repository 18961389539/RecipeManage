using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Application.Services;

public sealed class AuthService
{
    private readonly IAppDbContext _db;
    private readonly IPasswordHasher _passwords;
    private readonly ICurrentUser _user;
    private readonly LoginGuard _login;

    public AuthService(IAppDbContext db, IPasswordHasher passwords, ICurrentUser user, LoginGuard login)
    {
        _db = db;
        _passwords = passwords;
        _user = user;
        _login = login;
    }

    /// <summary>
    /// 整库备份前的把关：管理员 + 电子签名 + 审计留痕。
    ///
    /// 导出的是<strong>全部账号口令哈希与全部批记录</strong>，等同交出整套身份档案，
    /// 所以"是 Admin 就够了"并不够——被盗用的令牌或留在桌上没锁的会话都能一次拖走全库。
    /// 审计写在导出之前：失败的尝试同样值得留痕。
    /// </summary>
    public async Task RequireBackupEsignAsync(string? password, string fileName, CancellationToken ct)
    {
        EnsureAdmin();
        await new EsignGuard(_db, _user, _passwords).RequireAsync(password, ct);
        _db.AuditLogs.Add(new AuditLog(
            _user.UserId, _user.UserName, "admin.sqlite-backup", "Database", "",
            $"导出整库备份 {fileName}（含全部账号口令哈希与批记录）"));
        await _db.SaveChangesAsync(ct);
    }

    public async Task<AppUser> AuthenticateAsync(LoginRequest request, CancellationToken ct)
    {
        var name = request.UserName.Trim().ToLowerInvariant();
        var wait = _login.LockoutSeconds(name, DateTimeOffset.UtcNow);
        if (wait > 0)
        {
            await AuditLoginAsync(null, name, "auth.login.locked",
                $"连续失败已达上限，锁定窗口内还有 {wait}s 才接受下一次尝试。", ct);
            throw new DomainException("TOO_MANY_ATTEMPTS", $"失败次数过多，请 {wait} 秒后再试。");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserName == name, ct);
        // 不存在 / 已停用 / 密码错，对外同一句话：不能告诉调用者"这个用户名是有的"。
        if (user is null || !user.IsActive || !_passwords.Verify(request.Password, user.PasswordHash))
        {
            var failures = _login.RecordFailure(name, DateTimeOffset.UtcNow);
            await AuditLoginAsync(user?.Id, name, "auth.login.failed",
                $"用户名或密码错误（{LoginGuard.Window.TotalMinutes:0} 分钟内第 {failures} 次）。", ct);
            throw new DomainException("AUTH", "用户名或密码错误。");
        }

        _login.RecordSuccess(name);
        await AuditLoginAsync(user.Id, name, "auth.login", "登录成功。", ct);
        return user;
    }

    /// <summary>登录事件单独留痕：只记"谁在什么时候成功/失败"，绝不记口令。</summary>
    private async Task AuditLoginAsync(Guid? userId, string userName, string action, string detail, CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLog(userId, userName, action, "AppUser", userId?.ToString() ?? "", detail));
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken ct)
    {
        EnsureAdmin();
        var users = await _db.Users.ToListAsync(ct);
        return users.OrderBy(u => u.UserName).Select(ToDto).ToList();
    }

    public async Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct)
    {
        EnsureAdmin();
        if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Trim().Length < 3)
            throw new DomainException("USER", "用户名至少 3 个字符。");
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            throw new DomainException("USER", "密码至少 8 位。");
        var name = request.UserName.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.UserName == name, ct))
            throw new DomainException("DUP_USER", "用户名已存在。");

        var created = new AppUser(name, request.DisplayName, _passwords.Hash(request.Password), request.Role);
        _db.Users.Add(created);
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "user.create", "AppUser", created.Id.ToString(), name));
        await _db.SaveChangesAsync(ct);
        return ToDto(created);
    }

    public async Task<UserDto> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        EnsureAdmin();
        var target = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
                     ?? throw new DomainException("NOT_FOUND", "用户不存在。");

        if (!request.IsActive && target.Id == _user.UserId)
            throw new DomainException("USER", "不能停用当前登录账号。");
        if (!request.IsActive && target.Role == UserRole.Admin)
        {
            var otherAdmins = await _db.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsActive && u.Id != id, ct);
            if (otherAdmins == 0)
                throw new DomainException("USER", "不能停用最后一个启用的管理员。");
        }

        target.UpdateProfile(request.DisplayName, request.Role);
        target.SetActive(request.IsActive);
        if (!string.IsNullOrWhiteSpace(request.NewPassword))
        {
            if (request.NewPassword.Length < 8)
                throw new DomainException("USER", "密码至少 8 位。");
            target.ReplacePassword(_passwords.Hash(request.NewPassword));
        }

        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "user.update", "AppUser", target.Id.ToString(),
            $"{target.UserName}:{target.Role}:{(target.IsActive ? "active" : "disabled")}"));
        await _db.SaveChangesAsync(ct);
        return ToDto(target);
    }

    private void EnsureAdmin()
    {
        if (_user.Role is not UserRole.Admin)
            throw new DomainException("FORBIDDEN", "仅管理员可以管理用户。");
    }

    public static UserDto ToDto(AppUser user) =>
        new(user.Id, user.UserName, user.DisplayName, user.Role, user.IsActive);
}
