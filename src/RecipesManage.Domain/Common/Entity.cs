namespace RecipesManage.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; protected set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; protected set; }

    protected void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        OnTouch();
    }

    /// <summary>
    /// 供派生实体在每次状态变更时轮换自己的并发戳；默认无操作。
    /// </summary>
    protected virtual void OnTouch() { }
}

/// <summary>
/// 参与乐观并发控制的实体。每次成功写入都轮换 ConcurrencyStamp，
/// 使两个写者（HTTP 请求路径 与 后台调度线程）并发写同一行时，
/// 至少一方 UPDATE 命中 0 行并抛出 DbUpdateConcurrencyException，而不是静默覆盖。
/// </summary>
public interface IConcurrencyStamped
{
    Guid ConcurrencyStamp { get; }
    void RotateConcurrencyStamp();
}

public sealed class DomainException : Exception
{
    public string Code { get; }

    public DomainException(string code, string message) : base(message)
    {
        Code = code;
    }
}
