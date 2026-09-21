using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Identity;

public enum UserRole
{
    Admin = 0,
    ProcessEngineer = 1,
    Supervisor = 2,
    Quality = 3,
    Operator = 4
}

public sealed class AppUser : Entity
{
    public string UserName { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public UserRole Role { get; private set; }
    public bool IsActive { get; private set; } = true;

    private AppUser() { }

    public AppUser(string userName, string displayName, string passwordHash, UserRole role)
    {
        UserName = userName.Trim().ToLowerInvariant();
        DisplayName = displayName.Trim();
        PasswordHash = passwordHash;
        Role = role;
    }

    public void UpdateProfile(string displayName, UserRole role)
    {
        DisplayName = displayName.Trim();
        Role = role;
        Touch();
    }

    public void ReplacePassword(string passwordHash)
    {
        PasswordHash = passwordHash;
        Touch();
    }

    public void SetActive(bool active)
    {
        IsActive = active;
        Touch();
    }
}

public sealed class AuditLog : Entity
{
    public Guid? UserId { get; private set; }
    public string UserName { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public string EntityId { get; private set; } = string.Empty;
    public string? Detail { get; private set; }
    public DateTimeOffset At { get; private set; } = DateTimeOffset.UtcNow;

    private AuditLog() { }

    public AuditLog(Guid? userId, string userName, string action, string entityType, string entityId, string? detail)
    {
        UserId = userId;
        UserName = userName;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        Detail = detail;
    }
}
