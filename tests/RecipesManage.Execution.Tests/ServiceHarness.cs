using System.Collections.Concurrent;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Batches;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Services;
using RecipesManage.Infrastructure.Persistence;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 应用服务层测试的共用替身与建库。
///
/// 这些私有替身原先在四个测试类里各抄一份（RoleUser / NoopScheduler / NoopPdf / NoopPublisher / OpenDb），
/// 加一份"跳步安全门"测试就要抄第五份。集中在这里，替身的行为只有一处可改。
/// </summary>
internal static class ServiceHarness
{
    public static AppDbContext OpenDb(string prefix = "brmes-svc")
    {
        var db = CreateDb(prefix);
        SeedApprovalChain(db);
        return db;
    }

    /// <summary>
    /// EnsureCreated 只按模型建表、不跑迁移，所以默认审批链要手工种下去——
    /// 服务层的 SubmitAsync 刻意不在库里没有链时悄悄退回代码缺省链（那等于静默换掉"这版要谁签"）。
    /// 建库而不种链的私有 OpenDb 复制品会因此报 APPROVAL_CHAIN，不是回归，是在提醒它缺了这一步。
    /// </summary>
    public static void SeedApprovalChain(AppDbContext db, bool existingRowsAreFine = true)
    {
        if (existingRowsAreFine && db.ApprovalChains.Any())
            return;
        db.ApprovalChains.Add(ApprovalChainConfig.Create(
            "standard", "标准三级", ApprovalChain.Standard.Steps, isDefault: true, enabled: true));
        db.SaveChanges();
    }

    /// <summary>
    /// 批次写路径服务。参数顺序沿用拆分前的构造函数，所以各测试只需把 <c>new BatchService(</c> 换成它；
    /// 容器里 EsignGuard / BatchQueryService 是注入的，这里照同样的接法手工拼一遍。
    /// </summary>
    public static BatchService NewBatchService(
        IAppDbContext db, ICurrentUser user, IBatchScheduler scheduler, IPasswordHasher hasher,
        IBatchRecordPdf pdf, IExecutionPublisher publisher, MaterialLotService lots, EquipmentLeaseService leases) =>
        new(db, user, scheduler, publisher, lots, leases, new EsignGuard(db, user, hasher),
            new BatchQueryService(db, user, lots, pdf));

    /// <summary>EsignGuard 在容器里是注入的，这些工厂照同样的接法拼；参数顺序沿用拆分前的构造函数。</summary>
    public static RecipeService NewRecipeService(IAppDbContext db, ICurrentUser user, IPasswordHasher hasher) =>
        new(db, user, new EsignGuard(db, user, hasher));

    public static MaterialLotService NewMaterialLotService(IAppDbContext db, ICurrentUser user, IPasswordHasher hasher) =>
        new(db, user, new EsignGuard(db, user, hasher));

    public static ApprovalChainService NewApprovalChainService(IAppDbContext db, ICurrentUser user, IPasswordHasher hasher) =>
        new(db, user, new EsignGuard(db, user, hasher));

    /// <summary>批次读路径服务（列表 / 详情 / 趋势 / 批记录 / PDF / 报警列表）。</summary>
    public static BatchQueryService NewBatchQuery(
        IAppDbContext db, ICurrentUser user, MaterialLotService lots, IBatchRecordPdf? pdf = null) =>
        new(db, user, lots, pdf ?? new NoopPdf());

    private static AppDbContext CreateDb(string prefix)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.db")}")
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    public sealed class RoleUser(Guid id, UserRole role, string userName = "u", string displayName = "测试用户")
        : ICurrentUser
    {
        public Guid? UserId { get; } = id;
        public string UserName { get; } = userName;
        public string DisplayName { get; } = displayName;
        public UserRole? Role { get; } = role;
        public bool IsAuthenticated => true;
    }

    /// <summary>记录最后一次入队参数：安全门测试要断言"意图确实交给了调度器"。</summary>
    public sealed class RecordingScheduler : IBatchScheduler
    {
        public List<(Guid BatchId, string Reason, Guid? StepId)> Skips { get; } = [];

        public ValueTask EnqueueStartAsync(Guid batchId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueAbortAsync(Guid batchId, string reason, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueHoldAsync(Guid batchId, string reason, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask EnqueueSkipAsync(Guid batchId, string reason, Guid? stepId = null, CancellationToken cancellationToken = default)
        {
            Skips.Add((batchId, reason, stepId));
            return ValueTask.CompletedTask;
        }

        public ValueTask EnqueueConfirmAsync(Guid batchId, string comment, Guid? stepId = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    public sealed class NoopPdf : IBatchRecordPdf
    {
        public byte[] Render(BatchRecordDto record) => [];
    }

    public sealed class NoopPublisher : IExecutionPublisher
    {
        public List<ExecutionEvent> Events { get; } = [];

        public Task PublishAsync(ExecutionEvent evt, CancellationToken cancellationToken = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    /// <summary>要按事件类型做断言的测试用这个；<see cref="NoopPublisher"/> 是它的无序版本。</summary>
    public sealed class CapturingPublisher(ConcurrentBag<ExecutionEvent> sink) : IExecutionPublisher
    {
        public Task PublishAsync(ExecutionEvent evt, CancellationToken cancellationToken = default)
        {
            sink.Add(evt);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 最小可用批次：查询/计数类测试关心的是"这一行存在且处于某状态"，不是配方内容。
    /// 放在这里而不是各测试类里私有一份：快照构造那 20 行抄三遍，改模型时必漏一处。
    /// </summary>
    public static ProductionBatch Batch(string batchNo, Guid? equipmentId = null, Guid? createdBy = null)
    {
        var stepId = Guid.NewGuid();
        var snapshot = new ControlRecipeSnapshot
        {
            MasterRecipeId = Guid.NewGuid(),
            RecipeVersionId = Guid.NewGuid(),
            RecipeCode = "TST",
            RecipeName = "测试配方",
            ProductCode = "P",
            ProductName = "part",
            FrozenAt = DateTimeOffset.UtcNow,
            Steps =
            [
                new SnapshotStep
                {
                    StepId = stepId, Code = "S10", Name = "升温", Type = StepType.Heat,
                    Ordinal = 0, WatchdogSeconds = 60
                }
            ],
            Edges = []
        };
        var batch = ProductionBatch.Create(batchNo, equipmentId ?? Guid.NewGuid(), snapshot, "{}",
            createdBy ?? Guid.NewGuid());
        batch.StepExecutions.Add(new BatchStepExecution(batch.Id, stepId, "S10", "升温", StepType.Heat, 0));
        return batch;
    }
}
