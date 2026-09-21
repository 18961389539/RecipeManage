using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Services;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Persistence;

namespace RecipesManage.Execution;

public abstract record SchedulerCommand;
public sealed record StartBatchCommand(Guid BatchId) : SchedulerCommand;
public sealed record AbortBatchCommand(Guid BatchId, string Reason) : SchedulerCommand;
public sealed record HoldBatchCommand(Guid BatchId, string Reason) : SchedulerCommand;
public sealed record SkipStepCommand(Guid BatchId, string Reason, Guid? StepId) : SchedulerCommand;
public sealed record ConfirmStepCommand(Guid BatchId, string Comment, Guid? StepId) : SchedulerCommand;

public sealed partial class BatchSchedulerHostedService : BackgroundService, IBatchScheduler
{
    // "BRMES" 的 ASCII，作为全库唯一的 session 级 advisory lock key。
    private const long SchedulerLeaseLockKey = 0x42_52_4D_45_53;

    private readonly Channel<SchedulerCommand> _channel = Channel.CreateUnbounded<SchedulerCommand>(
        new UnboundedChannelOptions { SingleReader = true });
    // 会话写入在命令循环线程、移除在会话工作线程 finally —— 必须用并发集合，
    // 普通 Dictionary 跨线程读写会损坏内部结构。
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sessions = new();
    private readonly ConcurrentDictionary<Guid, string> _holdReasons = new();
    private readonly ConcurrentDictionary<Guid, SkipRequest> _skipRequests = new();
    private readonly ConcurrentDictionary<Guid, ConfirmRequest> _confirmRequests = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly IPlcDriverFactory _drivers;
    private readonly IExecutionPublisher _publisher;
    private readonly ILogger<BatchSchedulerHostedService> _log;
    private IServiceScope? _leaseScope;
    private DbConnection? _leaseConnection;

    public BatchSchedulerHostedService(
        IServiceScopeFactory scopes,
        IPlcDriverFactory drivers,
        IExecutionPublisher publisher,
        ILogger<BatchSchedulerHostedService> log)
    {
        _scopes = scopes;
        _drivers = drivers;
        _publisher = publisher;
        _log = log;
    }

    public ValueTask EnqueueStartAsync(Guid batchId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(new StartBatchCommand(batchId), cancellationToken);

    public ValueTask EnqueueAbortAsync(Guid batchId, string reason, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(new AbortBatchCommand(batchId, reason), cancellationToken);

    public ValueTask EnqueueHoldAsync(Guid batchId, string reason, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(new HoldBatchCommand(batchId, reason), cancellationToken);

    public ValueTask EnqueueSkipAsync(Guid batchId, string reason, Guid? stepId = null, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(new SkipStepCommand(batchId, reason, stepId), cancellationToken);

    public ValueTask EnqueueConfirmAsync(Guid batchId, string comment, Guid? stepId = null, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(new ConfirmStepCommand(batchId, comment, stepId), cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await AcquireSchedulerLeaseAsync(stoppingToken);
        try
        {
            await RecoverRunningAsync(stoppingToken);

            await foreach (var command in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                switch (command)
                {
                    case StartBatchCommand start:
                        _holdReasons.TryRemove(start.BatchId, out _);
                        _skipRequests.TryRemove(start.BatchId, out _);
                        StartSession(start.BatchId, stoppingToken);
                        break;
                    case ConfirmStepCommand confirm:
                        _confirmRequests[confirm.BatchId] = new ConfirmRequest(confirm.Comment, confirm.StepId);
                        break;
                    case HoldBatchCommand hold:
                        _holdReasons[hold.BatchId] = hold.Reason;
                        break;
                    case SkipStepCommand skip:
                        _skipRequests[skip.BatchId] = new SkipRequest(skip.Reason, skip.StepId);
                        break;
                    case AbortBatchCommand abort:
                        _holdReasons.TryRemove(abort.BatchId, out _);
                        _skipRequests.TryRemove(abort.BatchId, out _);
                        _confirmRequests.TryRemove(abort.BatchId, out _);
                        if (_sessions.TryRemove(abort.BatchId, out var cts))
                        {
                            cts.Cancel();
                            cts.Dispose();
                        }
                        await MarkAbortedAsync(abort.BatchId, abort.Reason, stoppingToken);
                        break;
                }
            }
        }
        finally
        {
            ReleaseSchedulerLease();
        }
    }

    /// <summary>
    /// 调度器单实例互斥：持有全库唯一的 session 级 advisory lock 后才允许恢复批次 / 消费命令。
    /// 后起的实例待机轮询，不驱动任何 PLC，消除两实例并发驱动同一设备的可能。
    /// 锁随连接存活；连接中断或进程退出时由 Postgres 自动释放，待机实例随后接管。
    /// </summary>
    private async Task AcquireSchedulerLeaseAsync(CancellationToken ct)
    {
        using (var probe = _scopes.CreateScope())
        {
            var provider = probe.ServiceProvider.GetRequiredService<AppDbContext>().Database.ProviderName;
            if (provider is not "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                // SQLite（测试/本地开发）无 advisory lock；单进程场景直接放行。
                _log.LogInformation("数据库提供方 {Provider} 不支持租约锁，跳过调度器互斥", provider);
                return;
            }
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync(ct);

            bool acquired;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
                var p = cmd.CreateParameter();
                p.ParameterName = "@key";
                p.Value = SchedulerLeaseLockKey;
                cmd.Parameters.Add(p);
                acquired = true.Equals(await cmd.ExecuteScalarAsync(ct));
            }

            if (acquired)
            {
                _leaseScope = scope;
                _leaseConnection = conn;
                _log.LogInformation("已获得调度器独占租约，开始恢复并驱动批次");
                return;
            }

            await conn.DisposeAsync();
            scope.Dispose();
            _log.LogWarning("调度器租约由其它实例持有，本机待机，5 秒后重试");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private void ReleaseSchedulerLease()
    {
        _leaseConnection?.Dispose();
        _leaseConnection = null;
        _leaseScope?.Dispose();
        _leaseScope = null;
    }

    private void StartSession(Guid batchId, CancellationToken hostCt)
    {
        if (_sessions.ContainsKey(batchId))
            return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        _sessions[batchId] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunBatchAsync(batchId, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                _log.LogInformation("批次 {BatchId} 会话已取消", batchId);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "批次 {BatchId} 调度异常", batchId);
                await MarkFaultAsync(batchId, "ENGINE", ex.Message, CancellationToken.None);
            }
            finally
            {
                // 按 (key, value) 移除：批次中止后可能被重新排队并立刻重启会话，
                // 按纯 key 删会误删新会话的 CTS，导致新会话无法被中止。
                _sessions.TryRemove(KeyValuePair.Create(batchId, cts));
                _skipRequests.TryRemove(batchId, out _);
                _confirmRequests.TryRemove(batchId, out _);
                cts.Dispose();
            }
        }, cts.Token);
    }

    private async Task RecoverRunningAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var running = await db.Batches
            .Where(b => b.Status == BatchStatus.Running || b.Status == BatchStatus.Queued)
            .Select(b => b.Id)
            .ToListAsync(ct);
        foreach (var id in running)
            await _channel.Writer.WriteAsync(new StartBatchCommand(id), ct);
    }

    private Task RunBatchAsync(Guid batchId, CancellationToken ct) => RunUnitWavesAsync(batchId, ct);

    /// <summary>
    /// 把状态机产生的动作落到 PLC 与数据库。lane 独占自己的 DbContext，无需任何跨 lane 锁。
    /// </summary>
    private async Task ApplyAsync(
        HandshakeAction action,
        LaneScope lane,
        HandshakeStateMachine machine,
        BatchStepExecution exec,
        SnapshotStep step,
        HandshakeWorkContext work,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var plc = lane.Plc;
        var db = lane.Db;
        var batch = lane.Batch;

        switch (action)
        {
            case WriteStepPayloadAction write:
                await plc.WriteStepPayloadAsync(write.StepId, write.StepType, write.Parameters, ct);
                var echo = await plc.ReadStepPayloadAsync(ct);
                if (!PlcWriteVerify.Matches(write.StepId, write.StepType, write.Parameters, echo, out var mismatch))
                {
                    machine.NotifyWriteVerifyFailed(now, mismatch);
                    RecordHandshake(lane, step, machine, work, now, "verify", mismatch);
                    return;
                }

                machine.NotifyParametersWritten(now);
                RecordHandshake(lane, step, machine, work, now, "write",
                    $"Step_ID={write.StepId} Step_Type={write.StepType}");
                RecordHandshake(lane, step, machine, work, now, "verify",
                    $"回读 Step_ID={echo.StepId} Param[0]={echo.Parameters.ElementAtOrDefault(0)} 一致");
                return;

            case AssertTriggerWriteAction trigger:
                if (trigger.Value && machine.Phase != HandshakePhase.AwaitingPlcAck)
                {
                    machine.NotifyTriggerAsserted();
                    RecordHandshake(lane, step, machine, work, now, "fault", machine.Fault?.Message);
                    return;
                }

                await plc.SetTriggerWriteAsync(trigger.Value, ct);
                if (trigger.Value)
                    machine.NotifyTriggerAsserted();
                RecordHandshake(lane, step, machine, work, now, "trigger",
                    trigger.Value ? "Trigger_Write=1" : "Trigger_Write=0");
                return;

            case ArchiveMeasurementsAction:
                var measured = await plc.ReadMeasuredAsync(ct);
                foreach (var (tag, value) in measured)
                    db.ProcessSamples.Add(new ProcessSample(batch.Id, step.StepId, now, tag, value, "archive"));
                exec.MarkCompleted(now, JsonSerializer.Serialize(QualityArchive.Bind(step, measured), BatchService.JsonOptions));
                machine.NotifyArchiveCompleted();
                RecordHandshake(lane, step, machine, work, now, "archive", "Step_Complete 归档实测");
                return;

            case ResetCompleteAction:
                await plc.ResetCompleteAsync(ct);
                machine.NotifyResetIssued();
                RecordHandshake(lane, step, machine, work, now, "reset", "复位 Step_Complete");
                return;

            case AdvanceStepAction:
                RecordHandshake(lane, step, machine, work, now, "advance", "允许下一步，禁止盲写");
                return;

            case RaiseFaultAction fault:
                RecordHandshake(lane, step, machine, work, now, "fault", fault.Fault.Message);
                return;
        }
    }

    private bool TryConsumeSkip(Guid batchId, Guid stepId, out string reason)
    {
        reason = "";
        if (!_skipRequests.TryGetValue(batchId, out var request))
            return false;
        if (request.StepId is Guid target && target != stepId)
            return false;
        if (!_skipRequests.TryRemove(batchId, out request))
            return false;
        reason = request.Reason;
        return true;
    }

    private bool TryConsumeConfirm(Guid batchId, Guid stepId, out string comment)
    {
        comment = "";
        if (!_confirmRequests.TryGetValue(batchId, out var request))
            return false;
        if (request.StepId is Guid target && target != stepId)
            return false;
        if (!_confirmRequests.TryRemove(batchId, out request))
            return false;
        comment = request.Comment;
        return true;
    }

    private readonly record struct SkipRequest(string Reason, Guid? StepId);
    private readonly record struct ConfirmRequest(string Comment, Guid? StepId);

    private static async Task IdlePlcAsync(IPlcHandshakeClient plc, CancellationToken ct)
    {
        try
        {
            await plc.SetHostHoldAsync(false, ct);
            await plc.SetTriggerWriteAsync(false, ct);
            await plc.ResetCompleteAsync(ct);
        }
        catch
        {
            // 保持/中止时尽量复位握手位，通讯失败不覆盖批次状态。
        }
    }

    /// <summary>
    /// 批次级写入。乐观并发冲突说明 HTTP 请求路径刚改过同一行：重载后以最新持久化状态为准，
    /// 不再反向覆盖操作员的指令。
    /// </summary>
    private async Task<bool> TrySaveBatchStateAsync(AppDbContext db, ProductionBatch batch, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            foreach (var entry in db.ChangeTracker.Entries()
                         .Where(e => e.State is not EntityState.Added and not EntityState.Detached).ToList())
                await entry.ReloadAsync(ct);

            _log.LogInformation("批次 {BatchId} 状态写入冲突，以最新持久化状态为准", batch.Id);
            return false;
        }
    }

    private async Task IdleEquipmentAsync(Guid equipmentId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var equipment = await db.Equipment.FirstOrDefaultAsync(e => e.Id == equipmentId, ct);
        if (equipment is null) return;
        try
        {
            await using var plc = _drivers.Create(equipment);
            await plc.ConnectAsync(ct);
            await IdlePlcAsync(plc, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "中止后复位设备 {EquipmentId} 握手位失败", equipmentId);
        }
    }

    private async Task MarkFaultAsync(Guid batchId, string code, string message, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var batch = await db.Batches.Include(b => b.StepExecutions).FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null) return;
        batch.Fault(code, message);
        var stepCode = batch.StepExecutions.FirstOrDefault(s => s.StepId == batch.CurrentStepId)?.StepCode ?? "";
        db.ProcessAlarms.Add(new ProcessAlarm(batch.Id, batch.BatchNo, batch.CurrentStepId, stepCode, code, "Fault", message, DateTimeOffset.UtcNow));
        if (!await TrySaveBatchStateAsync(db, batch, ct))
            return;
        await _publisher.PublishAsync(new ExecutionEvent(batchId, "fault", new { code, message }), ct);
        await _publisher.PublishAsync(new ExecutionEvent(batchId, "alarm", new
        {
            code,
            message,
            stepCode,
            severity = "Fault"
        }), ct);
        await OccupancyRealtime.PublishAsync(db, _publisher, batchId, ct);
    }

    private async Task MarkAbortedAsync(Guid batchId, string reason, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var batch = await db.Batches.FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null) return;
        if (batch.Status is not BatchStatus.Completed and not BatchStatus.Aborted)
        {
            batch.Abort(reason);
            await TrySaveBatchStateAsync(db, batch, ct);
            await ReleaseEquipmentAsync(batchId, ct);
            await _publisher.PublishAsync(new ExecutionEvent(batchId, "aborted", new { reason }), ct);
            await OccupancyRealtime.PublishAsync(db, _publisher, batchId, ct);
        }

        var snapshot = BatchService.Deserialize(batch.ControlRecipeJson);
        foreach (var equipmentId in UnitEquipmentBinding.AllIds(snapshot, batch.EquipmentId))
            await IdleEquipmentAsync(equipmentId, ct);
    }
}
