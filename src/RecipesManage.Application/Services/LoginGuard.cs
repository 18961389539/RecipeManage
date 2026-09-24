using System.Collections.Concurrent;

namespace RecipesManage.Application.Services;

/// <summary>
/// 登录失败限流。
///
/// 背景：演示口令是公开的（README 与 e2e helpers 里就写着 admin/Admin@123 这类），
/// 而登录接口此前既不节流也不留痕 —— 撞库不会被挡，事后也查不出"什么时候有人试过"。
///
/// 计数放在进程内存是有意的：本系统就是单实例部署（SQLite 单文件 + 进程内调度队列，
/// 见 RecipesDatabase 的说明），所以不需要分布式计数；重启清零对"挡住在线爆破"够用，
/// 真正的离线拖库要靠口令策略与备份管控，不是靠这个。
/// </summary>
public sealed class LoginGuard
{
    /// <summary>窗口内允许的失败次数。留得比正常误输宽得多，避免把自己人锁在门外。</summary>
    public const int MaxFailures = 8;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private sealed record Counter(int Failures, DateTimeOffset WindowStart);

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>还在锁定期内则返回剩余秒数，否则返回 0。</summary>
    public int LockoutSeconds(string userName, DateTimeOffset now)
    {
        if (!_counters.TryGetValue(userName, out var counter)) return 0;
        if (now - counter.WindowStart > Window)
        {
            _counters.TryRemove(userName, out _);
            return 0;
        }

        return counter.Failures < MaxFailures
            ? 0
            : (int)Math.Ceiling((counter.WindowStart + Window - now).TotalSeconds);
    }

    /// <summary>记一次失败，返回窗口内累计次数（用于审计文案）。</summary>
    public int RecordFailure(string userName, DateTimeOffset now)
    {
        var counter = _counters.AddOrUpdate(
            userName,
            _ => new Counter(1, now),
            (_, existing) => now - existing.WindowStart > Window
                ? new Counter(1, now)
                : existing with { Failures = existing.Failures + 1 });
        return counter.Failures;
    }

    public void RecordSuccess(string userName) => _counters.TryRemove(userName, out _);
}
