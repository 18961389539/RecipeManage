using System.Collections.Concurrent;
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

namespace RecipesManage.Execution;

public abstract record SchedulerCommand;
public sealed record StartBatchCommand(Guid BatchId) : SchedulerCommand;
public sealed record AbortBatchCommand(Guid BatchId, string Reason) : SchedulerCommand;
public sealed record HoldBatchCommand(Guid BatchId, string Reason) : SchedulerCommand;
public sealed record SkipStepCommand(Guid BatchId, string Reason, Guid? StepId) : SchedulerCommand;
public sealed record ConfirmStepCommand(Guid BatchId, string Comment, Guid? StepId) : SchedulerCommand;

public sealed partial class BatchSchedulerHostedService : BackgroundService, IBatchScheduler
{
    // 命令队列是进程内 Channel + 会话表：调度器只允许单实例运行（SQLite 单文件库同样
    // 决定了这一点）。若未来要横向扩展，需把命令通道与租约落到共享存储，而不是加回
    // 数据库咨询锁——提供方已固定为 SQLite。
    private readonly Channel<SchedulerCommand> _channel = Channel.CreateUnbounded<SchedulerCommand>(
        new UnboundedChannelOptions { SingleReader = true });
    // 会话写入在命令循环线程、移除在会话工作线程 finally —— 必须用并发集合，
    // 普通 Dictionary 跨线程读写会损坏内部结构。
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<Guid, string> _holdReasons = new();
    // 跳步 / 确认按工步存：并行车道可能同时有两个工步在等。StepId 为空的是"当前工步"通配。
    private readonly ConcurrentDictionary<StepKey, SkipRequest> _skipRequests = new();
    private readonly ConcurrentDictionary<StepKey, ConfirmRequest> _confirmRequests = new();

    /// <summary>
    /// 中止要等会话真的停下才能复位握手位、放租约。超时说明会话卡在某个不响应取消的 PLC 调用里，
    /// 这时宁可让设备继续被占，也不能把它交给下一批。
    /// </summary>
    private static readonly TimeSpan SessionStopTimeout = TimeSpan.FromSeconds(15);
    private readonly IServiceScopeFactory _scopes;
    private readonly IPlcDriverFactory _drivers;
    private readonly IExecutionPublisher _publisher;
    private readonly ILogger<BatchSchedulerHostedService> _log;

    /// <summary>
    /// 引擎唯一的时间出口：工步时长、保持窗口、读容忍窗、握手超时全部从这里取时间、用这里建定时器。
    /// 生产注册 TimeProvider.System（行为与直接 UtcNow 完全一致）；测试注入 FakeTimeProvider
    /// 即可虚拟推时——工步跑 1 秒不再真的等 1 秒，窗口断言也不再跟机器负载赛跑。
    /// </summary>
    internal TimeProvider Clock { get; }

    public BatchSchedulerHostedService(
        IServiceScopeFactory scopes,
        IPlcDriverFactory drivers,
        IExecutionPublisher publisher,
        ILogger<BatchSchedulerHostedService> log,
        TimeProvider? clock = null)
    {
        _scopes = scopes;
        _drivers = drivers;
        _publisher = publisher;
        _log = log;
        Clock = clock ?? TimeProvider.System;
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
        // 欠下的设备复位在后台补做：PLC 连不上时 PlcConnectRetry 可能要等很久，不能卡住命令循环。
        _ = Task.Run(() => PendingResetLoopAsync(stoppingToken), CancellationToken.None);
        await RecoverRunningAsync(stoppingToken);

        await foreach (var command in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            switch (command)
            {
                case StartBatchCommand start:
                    await HydrateIntentsAsync(start.BatchId, stoppingToken);
                    StartSession(start.BatchId, stoppingToken);
                    break;
                case ConfirmStepCommand confirm:
                    _confirmRequests[new StepKey(confirm.BatchId, confirm.StepId)] =
                        new ConfirmRequest(confirm.Comment);
                    break;
                case HoldBatchCommand hold:
                    _holdReasons[hold.BatchId] = hold.Reason;
                    break;
                case SkipStepCommand skip:
                    _skipRequests[new StepKey(skip.BatchId, skip.StepId)] = new SkipRequest(skip.Reason);
                    break;
                case AbortBatchCommand abort:
                    _holdReasons.TryRemove(abort.BatchId, out _);
                    ForgetStepRequests(abort.BatchId);
                    if (_sessions.TryRemove(abort.BatchId, out var session))
                    {
                        if (!await StopSessionAsync(abort.BatchId, session))
                        {
                            // 会话还没停：收尾挂到它真正结束之后，命令循环不能因一个批次卡住。
                            _ = session.Run!.ContinueWith(
                                _ =>
                                {
                                    session.Cts.Dispose();
                                    return MarkAbortedAsync(abort.BatchId, abort.Reason, CancellationToken.None);
                                },
                                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
                            break;
                        }
                    }
                    await MarkAbortedAsync(abort.BatchId, abort.Reason, stoppingToken);
                    break;
            }
        }
    }

    /// <summary>取消并等会话退出。返回 false 表示超时，会话仍可能在驱动 PLC。</summary>
    private async Task<bool> StopSessionAsync(Guid batchId, Session session)
    {
        session.Cts.Cancel();
        try
        {
            await session.Run!.WaitAsync(SessionStopTimeout);
        }
        catch (TimeoutException)
        {
            _log.LogWarning("批次 {BatchId} 会话 {Timeout} 内未响应中止，延后复位设备与释放租约",
                batchId, SessionStopTimeout);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "批次 {BatchId} 会话退出时异常", batchId);
        }
        session.Cts.Dispose();
        return true;
    }

    private void StartSession(Guid batchId, CancellationToken hostCt)
    {
        if (_sessions.ContainsKey(batchId))
            return;

        var session = new Session(CancellationTokenSource.CreateLinkedTokenSource(hostCt));
        _sessions[batchId] = session;
        var cts = session.Cts;
        session.Run = Task.Run(async () =>
        {
            try
            {
                await RunBatchAsync(batchId, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                _log.LogInformation("批次 {BatchId} 会话已取消", batchId);
            }
            catch (PlcCommLostException ex)
            {
                _log.LogError(ex, "批次 {BatchId} 读 PLC 持续失败，超出容忍窗口", batchId);
                await MarkFaultAsync(batchId, nameof(HandshakeFaultCode.PlcCommLost), ex.Message, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "批次 {BatchId} 调度异常", batchId);
                await MarkFaultAsync(batchId, "ENGINE", DescribeFault(ex), CancellationToken.None);
            }
            finally
            {
                // 按 (key, value) 移除：批次中止后可能被重新排队并立刻重启会话，
                // 按纯 key 删会误删新会话。谁移除成功谁负责释放 CTS——中止路径移除后还要用它等会话退出。
                if (_sessions.TryRemove(KeyValuePair.Create(batchId, session)))
                {
                    ForgetStepRequests(batchId);
                    cts.Dispose();
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// 故障消息要带上最内层原因：EF 的 "An error occurred while saving the entity changes" 只是外壳，
    /// 真正有用的是里面的 "database is locked" / "UNIQUE constraint failed"。操作员和现场排障只看得到这一行。
    /// </summary>
    internal static string DescribeFault(Exception ex)
    {
        var root = ex.GetBaseException();
        return ReferenceEquals(root, ex) ? ex.Message : $"{ex.Message} ← {root.GetType().Name}: {root.Message}";
    }

    private void ForgetStepRequests(Guid batchId)
    {
        foreach (var key in _skipRequests.Keys.Where(k => k.BatchId == batchId))
            _skipRequests.TryRemove(key, out _);
        foreach (var key in _confirmRequests.Keys.Where(k => k.BatchId == batchId))
            _confirmRequests.TryRemove(key, out _);
    }

    private async Task RecoverRunningAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var intents = await db.SchedulerIntents.AsNoTracking().ToListAsync(ct);
        ApplyIntents(intents);
        var running = await db.Batches
            .Where(b => b.Status == BatchStatus.Running || b.Status == BatchStatus.Queued)
            .Select(b => b.Id)
            .ToListAsync(ct);
        foreach (var id in running)
            await _channel.Writer.WriteAsync(new StartBatchCommand(id), ct);
    }

    private async Task HydrateIntentsAsync(Guid batchId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var rows = await db.SchedulerIntents.AsNoTracking()
            .Where(i => i.BatchId == batchId)
            .ToListAsync(ct);
        ApplyIntents(rows);
    }

    private void ApplyIntents(IEnumerable<SchedulerIntent> rows)
    {
        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case SchedulerIntentKinds.Hold:
                    _holdReasons[row.BatchId] = row.Reason;
                    break;
                case SchedulerIntentKinds.Skip:
                    _skipRequests[new StepKey(row.BatchId, row.StepId)] = new SkipRequest(row.Reason);
                    break;
                case SchedulerIntentKinds.Confirm:
                    _confirmRequests[new StepKey(row.BatchId, row.StepId)] = new ConfirmRequest(row.Reason);
                    break;
            }
        }
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
                // 写已经发出去了，读回来校验可以重试（读是幂等的）；写本身绝不重发。
                var echo = await ReadTolerantAsync(lane, step, machine, "写参回读", plc.ReadStepPayloadAsync, ct);
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
                var measured = await ReadTolerantAsync(lane, step, machine, "归档实测", plc.ReadMeasuredAsync, ct);
                foreach (var (tag, value) in measured)
                    db.ProcessSamples.Add(new ProcessSample(batch.Id, step.StepId, now, tag, value, "archive"));
                exec.MarkCompleted(now, JsonSerializer.Serialize(QualityArchive.Bind(step, measured), SnapshotJson.Options));
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

    /// <summary>
    /// 取走本工步的跳步请求（先找点名本工步的，再找通配的）。意图行的删除只登记在车道的 DbContext 上，
    /// 与工步被标成 Skipped 同一次落库：分开写的话，崩在两者之间，重启会把已执行的跳步再执行一遍。
    /// </summary>
    private async Task<string?> ConsumeSkipAsync(LaneScope lane, Guid stepId, CancellationToken ct)
    {
        if (!TryTakeStepRequest(_skipRequests, lane.Batch.Id, stepId, out var key, out var request))
            return null;
        await ForgetIntentAsync(lane, SchedulerIntentKinds.Skip, key.StepId, ct);
        return request.Reason;
    }

    /// <summary>同 <see cref="ConsumeSkipAsync"/>：删意图与工步完成同一次落库。</summary>
    private async Task<string?> ConsumeConfirmAsync(LaneScope lane, Guid stepId, CancellationToken ct)
    {
        if (!TryTakeStepRequest(_confirmRequests, lane.Batch.Id, stepId, out var key, out var request))
            return null;
        await ForgetIntentAsync(lane, SchedulerIntentKinds.Confirm, key.StepId, ct);
        return request.Comment;
    }

    private static bool TryTakeStepRequest<T>(
        ConcurrentDictionary<StepKey, T> requests, Guid batchId, Guid stepId, out StepKey key, out T request)
    {
        key = new StepKey(batchId, stepId);
        if (requests.TryRemove(key, out request!))
            return true;
        key = new StepKey(batchId, null);
        return requests.TryRemove(key, out request!);
    }

    /// <summary>只删 (批次, 种类, 工步) 这一行：按 (批次, 种类) 删会顺手删掉操作员刚签的、给别的工步的指令。</summary>
    private static async Task ForgetIntentAsync(LaneScope lane, string kind, Guid? stepId, CancellationToken ct)
    {
        var batchId = lane.Batch.Id;
        var rows = await lane.Db.SchedulerIntents
            .Where(i => i.BatchId == batchId && i.Kind == kind && i.StepId == stepId)
            .ToListAsync(ct);
        if (rows.Count > 0)
            lane.Db.SchedulerIntents.RemoveRange(rows);
    }

    private readonly record struct StepKey(Guid BatchId, Guid? StepId);
    private readonly record struct SkipRequest(string Reason);
    private readonly record struct ConfirmRequest(string Comment);

    private sealed class Session(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public Task? Run { get; set; }
    }

    private static async Task RemoveIntentsAsync(IAppDbContext db, Guid batchId, CancellationToken ct)
    {
        var rows = await db.SchedulerIntents.Where(i => i.BatchId == batchId).ToListAsync(ct);
        if (rows.Count > 0)
            db.SchedulerIntents.RemoveRange(rows);
    }

    private static async Task IdlePlcAsync(IPlcHandshakeClient plc, CancellationToken ct) =>
        await TryIdlePlcAsync(plc, ct);

    /// <summary>复位三个握手位。返回是否全部写成功；失败不抛，也不覆盖批次状态。</summary>
    private static async Task<bool> TryIdlePlcAsync(IPlcHandshakeClient plc, CancellationToken ct)
    {
        try
        {
            await plc.SetHostHoldAsync(false, ct);
            await plc.SetTriggerWriteAsync(false, ct);
            await plc.ResetCompleteAsync(ct);
            return true;
        }
        catch
        {
            // 保持/中止时尽量复位握手位，通讯失败不覆盖批次状态。
            return false;
        }
    }

    /// <summary>
    /// 批次级写入。乐观并发冲突说明 HTTP 请求路径刚改过同一行：重载后以最新持久化状态为准，
    /// 不再反向覆盖操作员的指令。
    /// </summary>
    private async Task<bool> TrySaveBatchStateAsync(IAppDbContext db, ProductionBatch batch, CancellationToken ct)
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

    /// <summary>连上设备并复位握手位。返回 true = 已确认复位（设备已不存在也算：没有可复位的东西了）。</summary>
    private async Task<bool> IdleEquipmentAsync(Guid equipmentId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var equipment = await db.Equipment.FirstOrDefaultAsync(e => e.Id == equipmentId, ct);
        if (equipment is null) return true;
        try
        {
            await using var plc = await PlcConnectRetry.ConnectAsync(
                () => _drivers.Create(equipment), equipment, _log, ct);
            var ok = await TryIdlePlcAsync(plc, ct);
            if (!ok)
                _log.LogWarning("复位设备 {EquipmentId} 握手位时写入失败", equipmentId);
            return ok;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "中止后复位设备 {EquipmentId} 握手位失败", equipmentId);
            return false;
        }
    }

    /// <summary>
    /// 每次重试之间的间隔。复位没成功的设备（PLC 连不上 / 写失败）带着持久标记留在库里，
    /// 这里决定多久再试一次。公开可写只是为了让测试不用等 30 秒。
    /// </summary>
    public TimeSpan PendingResetRetryInterval { get; set; } = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _pendingResetWake = new(0);

    /// <summary>
    /// 补做"欠下的设备复位"：中止时批次先定稿 Aborted、调度器稍后才复位设备；之间崩了或 PLC 连不上，
    /// 终态批次不会再被任何流程碰，设备上的握手位就一直留着。<see cref="PendingDeviceReset"/> 是那笔账的持久记录。
    /// 启动时先清一遍；清不掉的（PLC 仍连不上）隔一段时间再试，直到成功。
    /// </summary>
    private async Task PendingResetLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int remaining;
                try
                {
                    remaining = await SweepPendingResetsAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "补做设备复位时异常，稍后重试");
                    remaining = 1;
                }

                // 没有欠账就一直睡，直到某次中止后复位失败来叫醒；还有欠账就按间隔重试。
                await _pendingResetWake.WaitAsync(remaining == 0 ? Timeout.InfiniteTimeSpan : PendingResetRetryInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 停机
        }
    }

    /// <summary>清一遍欠账，返回仍没清掉的行数。</summary>
    private async Task<int> SweepPendingResetsAsync(CancellationToken ct)
    {
        List<PendingDeviceReset> rows;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            // SQLite 不能在库里按 DateTimeOffset 排序，行数也只有设备数那么多，取出来再排。
            rows = (await db.PendingDeviceResets.AsNoTracking().ToListAsync(ct)).OrderBy(r => r.CreatedAt).ToList();
        }

        var remaining = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

            var owner = await db.EquipmentLeases.AsNoTracking()
                .Where(l => l.EquipmentId == row.EquipmentId)
                .Select(l => (Guid?)l.BatchId)
                .FirstOrDefaultAsync(ct);
            if (owner == row.BatchId)
            {
                // 来源批次的中止收尾还在进行（等会话停下、尚未放租约）：它自己会复位，这一轮不碰。
                remaining++;
                continue;
            }

            var code = await db.Equipment.AsNoTracking()
                .Where(e => e.Id == row.EquipmentId).Select(e => e.Code).FirstOrDefaultAsync(ct)
                ?? row.EquipmentId.ToString("N")[..8];

            if (owner is not null)
            {
                // 设备已被别的批次占用：现在往上写复位会打断它的握手。残留由那个批次开工时的"残留握手位"检查处理。
                _log.LogInformation("设备 {Equipment} 欠的复位（来自批次 {BatchNo}）作废：已被其他批次占用", code, row.BatchNo);
                await DeletePendingResetAsync(db, row.EquipmentId, null, ct);
                continue;
            }

            if (!await IdleEquipmentAsync(row.EquipmentId, ct))
            {
                _log.LogWarning("设备 {Equipment} 欠的复位（来自批次 {BatchNo}）仍未成功，{Seconds:0}s 后重试",
                    code, row.BatchNo, PendingResetRetryInterval.TotalSeconds);
                remaining++;
                continue;
            }

            await DeletePendingResetAsync(db, row.EquipmentId,
                $"补做设备 {code} 握手位复位（{row.Reason}）", ct);
            _log.LogInformation("设备 {Equipment} 已补做握手位复位（{Reason}）", code, row.Reason);
        }

        return remaining;
    }

    /// <summary>复位确认成功后删标记；<paramref name="auditDetail"/> 非空时留一条系统审计。</summary>
    private static async Task DeletePendingResetAsync(
        IAppDbContext db, Guid equipmentId, string? auditDetail, CancellationToken ct)
    {
        var rows = await db.PendingDeviceResets.Where(r => r.EquipmentId == equipmentId).ToListAsync(ct);
        if (rows.Count == 0)
            return;
        db.PendingDeviceResets.RemoveRange(rows);
        if (auditDetail is not null)
            db.AuditLogs.Add(new AuditLog(null, "system", "device.reset", "EquipmentLine", equipmentId.ToString(), auditDetail));
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkFaultAsync(Guid batchId, string code, string message, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var batch = await db.Batches.Include(b => b.StepExecutions).FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null) return;
        if (batch.IsTerminal)
        {
            // 中止/放行与引擎抛错可能同 tick 撞车。此时批次事实已由人定稿，
            // Fault() 会覆盖中止原因与时间戳——只留服务端日志，不写库也不改状态。
            _log.LogWarning(
                "批次 {BatchId} 已处于终态 {Status}，引擎异常 {Code} 不再改写批次事实：{Message}",
                batch.Id, batch.Status, code, message);
            return;
        }
        batch.Fault(code, message);
        var stepCode = batch.StepExecutions.FirstOrDefault(s => s.StepId == batch.CurrentStepId)?.StepCode ?? "";
        db.ProcessAlarms.Add(new ProcessAlarm(batch.Id, batch.BatchNo, batch.CurrentStepId, stepCode, code, "Fault", message, Clock.GetUtcNow()));
        await RemoveIntentsAsync(db, batchId, ct);
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
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var batch = await db.Batches.FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null) return;
        var abortedHere = false;
        if (!batch.IsTerminal)
        {
            batch.Abort(reason);
            await RemoveIntentsAsync(db, batchId, ct);
            abortedHere = await TrySaveBatchStateAsync(db, batch, ct);
        }

        // 顺序有安全含义：先复位握手位、后放租约。反过来的话，租约一放另一批就能接管设备，
        // 这里的复位就会打断那一批的握手。调用前会话已确认退出，不会再有写入与复位交错。
        var snapshot = SnapshotJson.Deserialize(batch.ControlRecipeJson);
        var allIdle = true;
        foreach (var equipmentId in UnitEquipmentBinding.AllIds(snapshot, batch.EquipmentId))
        {
            if (await IdleEquipmentAsync(equipmentId, ct))
                await DeletePendingResetAsync(db, equipmentId, null, ct);
            else
                allIdle = false;   // 标记留在库里（BatchService 随定稿一起写的），由重试循环接着做
        }
        await ReleaseEquipmentAsync(batchId, ct);
        if (!allIdle)
            _pendingResetWake.Release();

        if (abortedHere)
            await _publisher.PublishAsync(new ExecutionEvent(batchId, "aborted", new { reason }), ct);
        await OccupancyRealtime.PublishAsync(db, _publisher, batchId, ct);
    }
}
