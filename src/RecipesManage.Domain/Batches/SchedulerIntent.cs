using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Batches;

public static class SchedulerIntentKinds
{
    public const string Hold = "Hold";
    public const string Skip = "Skip";
    public const string Confirm = "Confirm";
}

/// <summary>
/// 调度器进程内 Channel 在重启后会清空。把保持 / 跳步 / 人工确认落到这张表，
/// 恢复 Running 会话时才能继续执行，而不是把操作员已签名的指令丢掉。
/// 唯一键是 (批次, 种类, 工步)：并行车道上可以同时有两个工步在等确认 / 待跳过，
/// 按批次一行会让后到的指令覆盖先到的，被覆盖的车道就永远等不到它。
/// 保持是整批的（一条车道保持会经屏障让全批停下），所以它的 <see cref="StepId"/> 恒为空。
/// </summary>
public sealed class SchedulerIntent : Entity
{
    public Guid BatchId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public Guid? StepId { get; private set; }

    private SchedulerIntent() { }

    public SchedulerIntent(Guid batchId, string kind, string reason, Guid? stepId = null)
    {
        BatchId = batchId;
        Kind = kind;
        Reason = string.IsNullOrWhiteSpace(reason) ? kind : reason.Trim();
        StepId = stepId;
    }

    public void Replace(string reason, Guid? stepId)
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? Kind : reason.Trim();
        StepId = stepId;
        Touch();
    }
}
