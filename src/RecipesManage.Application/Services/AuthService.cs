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

    public AuthService(IAppDbContext db, IPasswordHasher passwords, ICurrentUser user)
    {
        _db = db;
        _passwords = passwords;
        _user = user;
    }

    public async Task<AppUser> AuthenticateAsync(LoginRequest request, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.UserName == request.UserName.Trim().ToLowerInvariant(), ct)
            ?? throw new DomainException("AUTH", "用户名或密码错误。");

        if (!user.IsActive || !_passwords.Verify(request.Password, user.PasswordHash))
            throw new DomainException("AUTH", "用户名或密码错误。");

        return user;
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
