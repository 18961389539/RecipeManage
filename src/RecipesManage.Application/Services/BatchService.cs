using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Handshake;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Application.Services;

/// <summary>
/// 批次的写路径：创建、启动、中止、保持、恢复、跳步、确认、放行、拒收、报警确认。
/// 每个入口的顺序都是 角色 → 电子签名 → 业务校验 → 落库 → 通知调度/推送。
///
/// 读路径（列表、详情、趋势、批记录、PDF……）在 <see cref="BatchQueryService"/>：
/// 本类返回详情时委托给它，反方向没有依赖。
/// <see cref="EsignGuard"/> 由容器注入而不是在构造函数里 <c>new</c>——此前这是三个服务各抄一份的地方，
/// 注入之后签名校验的替换与测试都有了接缝。
/// </summary>
public sealed class BatchService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IBatchScheduler _scheduler;
    private readonly IExecutionPublisher _publisher;
    private readonly MaterialLotService _lots;
    private readonly EquipmentLeaseService _leases;
    private readonly EsignGuard _esign;
    private readonly BatchQueryService _query;

    public BatchService(
        IAppDbContext db,
        ICurrentUser user,
        IBatchScheduler scheduler,
        IExecutionPublisher publisher,
        MaterialLotService lots,
        EquipmentLeaseService leases,
        EsignGuard esign,
        BatchQueryService query)
    {
        _db = db;
        _user = user;
        _scheduler = scheduler;
        _publisher = publisher;
        _lots = lots;
        _leases = leases;
        _esign = esign;
        _query = query;
    }

    private Task<BatchDetailDto> GetAsync(Guid id, CancellationToken ct) => _query.GetAsync(id, ct);

    public async Task<BatchDetailDto> CreateAsync(CreateBatchRequest request, CancellationToken ct)
    {
        EnsureCan(Capabilities.BatchOperate);
        if (await _db.Batches.AnyAsync(b => b.BatchNo == request.BatchNo, ct))
            throw new DomainException("DUP_BATCH", "批次号已存在。");

        var recipe = await _db.Recipes
            .Include(r => r.Versions).ThenInclude(v => v.Steps).ThenInclude(s => s.Parameters)
            .Include(r => r.Versions).ThenInclude(v => v.Edges)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == request.RecipeId, ct)
            ?? throw new DomainException("NOT_FOUND", "配方不存在。");

        var version = recipe.Versions.SingleOrDefault(v => v.Id == recipe.CurrentApprovedVersionId)
                      ?? throw new DomainException("NO_APPROVED", "没有已批准的主配方版本，无法生成控制配方。");

        var equipment = await _db.Equipment.FirstOrDefaultAsync(e => e.Id == request.EquipmentId, ct)
                        ?? throw new DomainException("NO_EQ", "设备不存在。");
        if (!equipment.Enabled)
            throw new DomainException("EQ_DISABLED", "设备未启用。");

        var snapshot = ControlRecipeSnapshotFactory.From(
            recipe, version, DateTimeOffset.UtcNow, request.ScaleFactor, request.LotNumber,
            request.UnitEquipment, request.EquipmentId);
        foreach (var equipmentId in UnitEquipmentBinding.AllIds(snapshot, request.EquipmentId))
        {
            if (equipmentId == request.EquipmentId)
                continue;
            var extra = await _db.Equipment.FirstOrDefaultAsync(e => e.Id == equipmentId, ct)
                        ?? throw new DomainException("NO_EQ", "单元绑定的设备不存在。");
            if (!extra.Enabled)
                throw new DomainException("EQ_DISABLED", $"单元设备 {extra.Code} 未启用。");
        }
        await EnsureEquipmentClassAsync(snapshot, request.EquipmentId, ct);
        SnapshotIntegrity.Seal(snapshot, SnapshotJson.Options, out var json);
        var batch = ProductionBatch.Create(request.BatchNo.Trim(), equipment.Id, snapshot, json, _user.UserId ?? Guid.Empty);

        foreach (var step in snapshot.Steps)
            batch.StepExecutions.Add(new BatchStepExecution(batch.Id, step.StepId, step.Code, step.Name, step.Type, step.Ordinal));

        _db.Batches.Add(batch);
        await _lots.BindSnapshotLotsAsync(batch, snapshot.ProductCode, snapshot.ProductName, request.LotNumber, request.ChargeLotIds, ct);
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "batch.create", "ProductionBatch", batch.Id.ToString(),
            $"{batch.BatchNo} scale={request.ScaleFactor} lot={request.LotNumber} units={snapshot.UnitEquipment?.Count ?? 0}"));
        await _db.SaveChangesAsync(ct);
        return await GetAsync(batch.Id, ct);
    }

    public async Task<BatchDetailDto> StartAsync(Guid id, string password, CancellationToken ct)
    {
        EnsureCan(Capabilities.BatchOperate);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        var snapshot = SnapshotJson.Deserialize(batch.ControlRecipeJson)
                       ?? throw new DomainException("SNAPSHOT", "控制配方快照损坏。");
        SnapshotIntegrity.DemandSealed(SnapshotIntegrity.Verify(snapshot, SnapshotJson.Options));
        await EnsureTagMapCoversSnapshotAsync(batch, snapshot, ct);
        await AcquireEquipmentAsync(batch, ct);

        var retry = batch.Status == BatchStatus.Faulted;
        batch.Queue();
        AuditEsign(retry ? "batch.retry.esign" : "batch.start.esign", batch.Id, batch.BatchNo);
        await SaveBatchStateAsync(ct);
        await OccupancyRealtime.PublishAsync(_db, _publisher, batch.Id, ct);
        await _scheduler.EnqueueStartAsync(batch.Id, ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> AbortAsync(Guid id, string reason, string password, CancellationToken ct)
    {
        EnsureCan(Capabilities.BatchOperate);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        batch.Abort(string.IsNullOrWhiteSpace(reason) ? "操作员中止" : reason);
        await ClearIntentsAsync(batch.Id, ct);
        AuditEsign("batch.abort.esign", batch.Id, reason);
        await SaveBatchStateAsync(ct);
        // 租约不在这里放：会话可能还在写 PLC，此时放租约，另一批就能接管同一台设备，
        // 而调度器随后的握手位复位会打在新批次身上。停会话 → 复位 → 放租约由调度器按序做。
        await _scheduler.EnqueueAbortAsync(batch.Id, reason, ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> HoldAsync(Guid id, string reason, string password, CancellationToken ct)
    {
        EnsureCan(Capabilities.BatchOperate);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        if (batch.Status == BatchStatus.Queued)
        {
            batch.Hold(reason);
            AuditEsign("batch.hold.esign", batch.Id, reason);
            await SaveBatchStateAsync(ct);
            await OccupancyRealtime.PublishAsync(_db, _publisher, batch.Id, ct);
        }
        else if (batch.Status == BatchStatus.Running)
        {
            var holdReason = string.IsNullOrWhiteSpace(reason) ? "操作员保持" : reason.Trim();
            await UpsertIntentAsync(batch.Id, SchedulerIntentKinds.Hold, holdReason, null, ct);
            AuditEsign("batch.hold.esign", batch.Id, holdReason);
            await SaveBatchStateAsync(ct);
            await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "hold-requested", new
            {
                reason = holdReason,
                status = batch.Status.ToString()
            }), ct);
            await _scheduler.EnqueueHoldAsync(batch.Id, holdReason, ct);
        }
        else
            throw new DomainException("CANNOT_HOLD", $"批次状态 {batch.Status} 不能保持。");
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> ResumeAsync(Guid id, string password, CancellationToken ct)
    {
        EnsureCan(Capabilities.BatchOperate);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        await AcquireEquipmentAsync(batch, ct);
        batch.Resume();
        await ClearIntentsAsync(batch.Id, ct, SchedulerIntentKinds.Hold);
        AuditEsign("batch.resume.esign", batch.Id, batch.BatchNo);
        await SaveBatchStateAsync(ct);
        await OccupancyRealtime.PublishAsync(_db, _publisher, batch.Id, ct);
        await _scheduler.EnqueueStartAsync(batch.Id, ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> SkipAsync(Guid id, string reason, string password, Guid? stepId, CancellationToken ct)
    {
        EnsureCan(Capabilities.BatchSkip);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        var skipReason = string.IsNullOrWhiteSpace(reason) ? "主管跳步" : reason.Trim();
        var snapshot = SnapshotJson.Deserialize(batch.ControlRecipeJson)
                       ?? throw new DomainException("SNAPSHOT", "控制配方快照损坏。");
        var target = ResolveSkipTarget(batch, snapshot, stepId);
        var exec = batch.StepExecutions.Single(s => s.StepId == target.StepId);
        var equipmentId = UnitEquipmentBinding.Resolve(snapshot, target.UnitProcedure, batch.EquipmentId);
        var laneRow = await _db.Lanes.AsNoTracking()
            .FirstOrDefaultAsync(l => l.BatchId == batch.Id && l.EquipmentId == equipmentId, ct);

        if (batch.Status == BatchStatus.Running)
        {
            if (exec.Outcome is StepOutcome.Completed or StepOutcome.Skipped)
                throw new DomainException("SKIP_DONE", "当前工步已完成，不能跳过。");
            if (exec.Outcome is not (StepOutcome.Running or StepOutcome.AwaitingConfirm))
                throw new DomainException("SKIP_UNSAFE", "只能跳过正在等待 PLC_Ready、等待或人工确认的工步，禁止跨单元误跳邻道。");
            // 车道相位的唯一真源是 batch_lanes 行：引擎在 MarkRunning 之前就给每条绑定设备建行
            // （EnsureLaneRowsAsync），所以"在跑却没有行"只可能是数据被外力破坏。
            // 这时一律按不可跳处理——宁可让操作员先保持再跳，也不能拿展示串或历史事件猜一个相位去盲写 PLC。
            if (laneRow is null)
                throw new DomainException("SKIP_UNSAFE",
                    "该车道还没有被执行引擎接管，不能跳步。请等批次进入握手状态后再试。");
            if (!BatchLanes.IsSkipSafePhase(laneRow.Phase))
                throw new DomainException("SKIP_UNSAFE",
                    $"车道 {laneRow.EquipmentCode} 当前相位 {laneRow.Phase}，禁止跳步盲写。请先保持，待 PLC_Ready / 等待 / 人工确认后再跳过。");
            await UpsertIntentAsync(batch.Id, SchedulerIntentKinds.Skip, skipReason, target.StepId, ct);
            AuditEsign("batch.skip.esign", batch.Id, $"{skipReason} step={target.Code}");
            await SaveBatchStateAsync(ct);
            await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "skip-requested", new
            {
                reason = skipReason,
                stepId = target.StepId,
                status = batch.Status.ToString()
            }), ct);
            await _scheduler.EnqueueSkipAsync(batch.Id, skipReason, target.StepId, ct);
            return await GetAsync(id, ct);
        }

        if (batch.Status is not BatchStatus.Held and not BatchStatus.Faulted)
            throw new DomainException("CANNOT_SKIP", $"批次状态 {batch.Status} 不能跳步。");

        if (exec.Outcome is StepOutcome.Completed)
            throw new DomainException("SKIP_DONE", "当前工步已完成，不能跳过。");
        // 履历里的相位同样只认车道行；没有行（批次还没被接管）时按工步结论映射，
        // 绝不再写批次展示串——那是 "HT-A:Held · HT-B:…" 这种拼串，写进历史会污染按相位重建的读侧。
        var phaseForRecord = laneRow?.Phase ?? HandshakeView.FromStepOutcome(exec.Outcome);
        exec.MarkSkipped(skipReason);
        _db.HandshakeEvents.Add(new HandshakeEvent(batch.Id, target.StepId, target.Code, phaseForRecord, "skip", skipReason, null));
        var finished = false;
        if (batch.StepExecutions.All(s => s.Outcome is StepOutcome.Completed or StepOutcome.Skipped))
        {
            batch.Complete(DateTimeOffset.UtcNow);
            finished = true;
        }
        else
        {
            var next = snapshot.Steps.FirstOrDefault(s =>
                batch.StepExecutions.Single(e => e.StepId == s.StepId).Outcome is StepOutcome.Pending or StepOutcome.Running or StepOutcome.Faulted or StepOutcome.Held);
            if (next is not null)
                batch.AdvanceTo(next.StepId, snapshot.Steps.ToList().FindIndex(s => s.StepId == next.StepId));
            batch.Queue();
            await _scheduler.EnqueueStartAsync(batch.Id, ct);
        }
        AuditEsign("batch.skip.esign", batch.Id, $"{skipReason} step={target.Code}");
        await SaveBatchStateAsync(ct);
        if (finished)
            await _leases.ReleaseAsync(batch.Id, ct);
        await OccupancyRealtime.PublishAsync(_db, _publisher, batch.Id, ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> ConfirmAsync(Guid id, string comment, string password, Guid? stepId, CancellationToken ct)
    {
        EnsureCan(Capabilities.BatchConfirm);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        if (batch.Status != BatchStatus.Running)
            throw new DomainException("CANNOT_CONFIRM", "只有运行中的批次可以人工确认。");
        var snapshot = SnapshotJson.Deserialize(batch.ControlRecipeJson)
                       ?? throw new DomainException("SNAPSHOT", "控制配方快照损坏。");
        var awaiting = batch.StepExecutions
            .Where(e => e.Outcome is StepOutcome.AwaitingConfirm or StepOutcome.Running)
            .Select(e => e.StepId)
            .ToHashSet();
        var target = stepId is Guid sid
            ? snapshot.Steps.FirstOrDefault(s => s.StepId == sid)
            : snapshot.Steps.FirstOrDefault(s => s.Type == StepType.ManualConfirm && awaiting.Contains(s.StepId));
        if (target is null)
            throw new DomainException("NOT_MANUAL", "当前没有等待确认的人工确认工步。");
        if (target.Type != StepType.ManualConfirm)
            throw new DomainException("NOT_MANUAL", "当前工步不是人工确认，禁止当作写参工步确认。");
        var exec = batch.StepExecutions.Single(s => s.StepId == target.StepId);
        if (exec.Outcome is StepOutcome.Completed or StepOutcome.Skipped)
            throw new DomainException("CONFIRM_DONE", "该人工确认工步已结束。");
        var confirmComment = string.IsNullOrWhiteSpace(comment) ? "操作员确认" : comment.Trim();
        await UpsertIntentAsync(batch.Id, SchedulerIntentKinds.Confirm, confirmComment, target.StepId, ct);
        AuditEsign("batch.confirm.esign", batch.Id, string.IsNullOrWhiteSpace(comment) ? target.Code : comment);
        await SaveBatchStateAsync(ct);
        await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "confirm-requested", new
        {
            comment = confirmComment,
            stepId = target.StepId,
            status = batch.Status.ToString()
        }), ct);
        await _scheduler.EnqueueConfirmAsync(batch.Id, confirmComment, target.StepId, ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> ReleaseAsync(Guid id, string comment, string password, CancellationToken ct)
    {
        EnsureCan(Capabilities.QualityDisposition);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        var snapshot = SnapshotJson.Deserialize(batch.ControlRecipeJson)
                       ?? throw new DomainException("SNAPSHOT", "控制配方快照损坏。");
        SnapshotIntegrity.DemandSealed(SnapshotIntegrity.Verify(snapshot, SnapshotJson.Options));
        var labs = await _db.LabSamples.AsNoTracking().Where(s => s.BatchId == batch.Id).ToListAsync(ct);
        if (QualityDisposition.HasPendingFinalSample(labs))
            throw new DomainException("LAB_PENDING", "终检样品尚未判定，不能放行。");
        if ((QualityDisposition.HasOutOfSpec(snapshot, batch.StepExecutions) || QualityDisposition.HasFailedLabSample(labs))
            && string.IsNullOrWhiteSpace(comment))
        {
            // 把"没测到"和"测到不合格"分开说：前者是这条规格压根没被评价过，
            // 让质量对着它写一句"检验合格"就放行，是履历上最坏的一种安静失败。
            var unarchived = QualityDisposition.UnarchivedSpecs(snapshot, batch.StepExecutions);
            if (unarchived.Count > 0)
                throw new DomainException("QUALITY_OOS",
                    $"以下规格从未取到实测值，等于没有被评价过：{string.Join("、", unarchived)}。" +
                    "请为该参数声明实测点（或把实验室指标改建为质检样品）后重新跑批；" +
                    "确需对这批评偏差放行，必须填写意见说明原因。");
            throw new DomainException("QUALITY_OOS", "归档质检或实验室样品超差，偏差放行必须填写意见。");
        }
        batch.Release(_user.UserName ?? "quality", comment, DateTimeOffset.UtcNow);
        await _lots.ApplyBatchDispositionAsync(batch, ct);
        AuditEsign("batch.release.esign", batch.Id, batch.ReleaseComment, _user.UserName ?? "quality");
        await SaveBatchStateAsync(ct);
        await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "released", new { batch.BatchNo, status = batch.Status.ToString() }), ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> RejectDispositionAsync(Guid id, string reason, string password, CancellationToken ct)
    {
        EnsureCan(Capabilities.QualityDisposition);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        batch.RejectDisposition(_user.UserName ?? "quality", reason, DateTimeOffset.UtcNow);
        await _lots.ApplyBatchDispositionAsync(batch, ct);
        AuditEsign("batch.reject.esign", batch.Id, batch.ReleaseComment, _user.UserName ?? "quality");
        await SaveBatchStateAsync(ct);
        await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "disposition-rejected", new { batch.BatchNo, status = batch.Status.ToString() }), ct);
        return await GetAsync(id, ct);
    }

    private static SnapshotStep ResolveSkipTarget(ProductionBatch batch, ControlRecipeSnapshot snapshot, Guid? stepId)
    {
        if (stepId is Guid id)
            return snapshot.Steps.FirstOrDefault(s => s.StepId == id)
                   ?? throw new DomainException("SKIP_STEP", "指定工步不在本批次控制配方快照中。");
        if (batch.CurrentStepId is Guid current)
        {
            var match = snapshot.Steps.FirstOrDefault(s => s.StepId == current);
            if (match is not null)
                return match;
        }

        var running = batch.StepExecutions.FirstOrDefault(s => s.Outcome is StepOutcome.Running or StepOutcome.AwaitingConfirm);
        if (running is not null)
            return snapshot.Steps.Single(s => s.StepId == running.StepId);

        var index = Math.Clamp(batch.CurrentStepIndex, 0, Math.Max(snapshot.Steps.Count - 1, 0));
        return snapshot.Steps[index];
    }


    public async Task<ProcessAlarmDto> AcknowledgeAlarmAsync(Guid alarmId, CancellationToken ct)
    {
        EnsureCan(Capabilities.AlarmAck);
        var alarm = await _db.ProcessAlarms.FirstOrDefaultAsync(a => a.Id == alarmId, ct)
                    ?? throw new DomainException("NOT_FOUND", "报警不存在。");
        alarm.Acknowledge(_user.DisplayName, DateTimeOffset.UtcNow);
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "alarm.ack", "ProcessAlarm", alarm.Id.ToString(),
            alarm.Code));
        await _db.SaveChangesAsync(ct);
        var dto = BatchQueryService.MapAlarm(alarm);
        await _publisher.PublishAsync(new ExecutionEvent(alarm.BatchId, "alarm", dto), ct);
        return dto;
    }



    /// <summary>含义原文在这一刻冻结进 signature_records；之后展示读库里的文本，不再查当前代码里的含义表。</summary>
    private void AuditEsign(string action, Guid batchId, string? extra, string? userName = null) =>
        _esign.Record(action, "ProductionBatch", batchId.ToString(), ElectronicSignature.Batch(action), extra, userName);


    private async Task EnsureEquipmentClassAsync(ControlRecipeSnapshot snapshot, Guid primaryEquipmentId, CancellationToken ct)
    {
        var boundIds = UnitEquipmentBinding.AllIds(snapshot, primaryEquipmentId);
        var equipment = await _db.Equipment.AsNoTracking()
            .Where(e => boundIds.Contains(e.Id))
            .ToListAsync(ct);
        if (equipment.All(e => string.IsNullOrWhiteSpace(e.EquipmentClassCode)))
            return;

        var classes = await _db.EquipmentClasses.Include(c => c.Templates).AsNoTracking().ToListAsync(ct);
        var allowed = classes.ToDictionary(
            c => c.Code,
            c => (IReadOnlySet<int>)c.AllowedProgramIds(),
            StringComparer.OrdinalIgnoreCase);
        EquipmentClassRules.EnsureCompatible(
            snapshot.Steps.Select(s => (s.Code, s.Type, s.PlcProgramId, s.UnitProcedure, s.EquipmentClassCode)),
            unit => UnitEquipmentBinding.Resolve(snapshot, unit, primaryEquipmentId),
            equipment.ToDictionary(e => e.Id, e => e.Code),
            equipment.ToDictionary(e => e.Id, e => e.EquipmentClassCode),
            allowed);
    }

    /// <summary>
    /// 占用由码设备排他：插入 <c>equipment_leases</c> 成功才算占用。
    /// 替换原先的 EnsureEquipmentFreeAsync —— 那个实现先 SELECT 再写，
    /// 两名操作员同时点启动时双方都能通过检查，最终两个批次写同一台 PLC。
    /// </summary>
    private async Task AcquireEquipmentAsync(ProductionBatch batch, CancellationToken ct)
    {
        var ids = SnapshotJson.BoundEquipmentIds(batch);
        var codes = await _db.Equipment
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Code, ct);
        await _leases.AcquireAsync(batch, ids, codes, ct);
    }

    /// <summary>
    /// 点表必须真的承载得住这份配方：参数声明的实测点要在点表里，写参帧用到的槽位要在点表槽数之内。
    /// 两项都拒绝启动而不是告警——漏归档会写进电子批记录、漏写参会让设备带着上一步的设定值跑，事后都无从发现。
    /// 放在取设备租约之前，校验失败不会把租约漏在那儿。
    /// </summary>
    private async Task EnsureTagMapCoversSnapshotAsync(
        ProductionBatch batch,
        ControlRecipeSnapshot snapshot,
        CancellationToken ct)
    {
        var steps = snapshot.Steps
            .Select(s => (Step: s, EquipmentId: UnitEquipmentBinding.Resolve(snapshot, s.UnitProcedure, batch.EquipmentId)))
            .ToList();

        var ids = steps.Select(s => s.EquipmentId).Distinct().ToList();
        var rows = await _db.Equipment.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.Code, e.TagMapJson })
            .ToListAsync(ct);

        var maps = new Dictionary<Guid, (string Code, HandshakeTagMap Map)>();
        foreach (var row in rows)
        {
            try
            {
                maps[row.Id] = (row.Code, TagMapValidator.Parse(row.TagMapJson));
            }
            catch (DomainException ex)
            {
                throw new DomainException(ex.Code, $"设备 {row.Code} {ex.Message}");
            }
        }

        var gaps = new List<string>();
        foreach (var (step, equipmentId) in steps)
        {
            if (!maps.TryGetValue(equipmentId, out var bound))
                throw new DomainException("TAGMAP", $"工步 {step.Code} 绑定的设备不存在，无法校验点表。");

            // 归档来源包括"推断出来的"那一半：只查显式声明的标签，等于让非热处工艺
            // （名字里没有温度/压力字样）绕过这道校验，最后以"超差"的面目在放行页才暴露。
            var declared = step.Parameters
                .Where(p => p.ArchiveAsQuality)
                .Select(p => QualityArchive.ResolveSourceTag(p.Name, p.EngineeringUnit, p.Semantic, p.MeasuredTag))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!)
                .ToList();
            foreach (var tag in TagMapValidator.MissingMeasuredTags(bound.Map, declared))
                gaps.Add($"工步 {step.Code} 的实测点 {tag} 不在设备 {bound.Code} 的点表里");

            var beyond = TagMapValidator.SlotsBeyondMap(bound.Map, ControlRecipeWritePlan.WrittenSlots(step));
            if (beyond.Count > 0)
                gaps.Add(
                    $"工步 {step.Code} 要写槽位 {string.Join("/", beyond)}，" +
                    $"但设备 {bound.Code} 的点表只有 {bound.Map.Params.Count} 个槽");
        }

        if (gaps.Count > 0)
            throw new DomainException(
                "TAGMAP",
                $"设备点表承载不了这份配方：{string.Join("；", gaps.Distinct(StringComparer.Ordinal))}。" +
                "请补点表地址，或改掉配方参数。");
    }


    private async Task UpsertIntentAsync(Guid batchId, string kind, string reason, Guid? stepId, CancellationToken ct)
    {
        var row = await _db.SchedulerIntents.FirstOrDefaultAsync(
            i => i.BatchId == batchId && i.Kind == kind && i.StepId == stepId, ct);
        if (row is null)
            _db.SchedulerIntents.Add(new SchedulerIntent(batchId, kind, reason, stepId));
        else
            row.Replace(reason, stepId);
    }

    private async Task ClearIntentsAsync(Guid batchId, CancellationToken ct, params string[] kinds)
    {
        var rows = await _db.SchedulerIntents.Where(i => i.BatchId == batchId).ToListAsync(ct);
        if (kinds.Length > 0)
            rows = rows.Where(i => kinds.Contains(i.Kind)).ToList();
        if (rows.Count > 0)
            _db.SchedulerIntents.RemoveRange(rows);
    }

    /// <summary>
    /// 批次状态写入。并发令牌让"调度线程覆盖操作员指令"这件事显式失败，
    /// 而不是静默丢掉操作员的指令。
    /// </summary>
    private async Task SaveBatchStateAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException("CONFLICT", "批次状态已被调度引擎并发更新，请刷新后重试。");
        }
    }

    private Task<ProductionBatch> LoadAsync(Guid id, CancellationToken ct) => _query.LoadAsync(id, ct);

    private Task RequireEsignAsync(string password, CancellationToken ct) => _esign.RequireAsync(password, ct);

    private void EnsureCan(Capability capability) => _esign.EnsureCan(capability);
}
