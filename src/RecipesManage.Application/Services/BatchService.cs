using System.Linq.Expressions;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Application.Services;

public sealed class BatchService
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IBatchScheduler _scheduler;
    private readonly IBatchRecordPdf _pdf;
    private readonly IExecutionPublisher _publisher;
    private readonly MaterialLotService _lots;
    private readonly EquipmentLeaseService _leases;
    private readonly EsignGuard _esign;

    public BatchService(
        IAppDbContext db,
        ICurrentUser user,
        IBatchScheduler scheduler,
        IPasswordHasher passwords,
        IBatchRecordPdf pdf,
        IExecutionPublisher publisher,
        MaterialLotService lots,
        EquipmentLeaseService leases)
    {
        _db = db;
        _user = user;
        _scheduler = scheduler;
        _pdf = pdf;
        _publisher = publisher;
        _lots = lots;
        _leases = leases;
        _esign = new EsignGuard(db, user, passwords);
    }

    public async Task<IReadOnlyList<BatchListItemDto>> ListAsync(CancellationToken ct)
    {
        List<ProductionBatch> batches;
        if (_db.SupportsServerDateOrdering)
        {
            batches = await _db.Batches.AsNoTracking()
                .OrderByDescending(b => b.CreatedAt).Take(200).ToListAsync(ct);
        }
        else
        {
            batches = (await _db.Batches.AsNoTracking().ToListAsync(ct))
                .OrderByDescending(b => b.CreatedAt).Take(200).ToList();
        }

        // 只取本页批次引用到的主数据，不再整表加载设备/配方字典。
        var equipmentIds = batches.Select(b => b.EquipmentId).ToHashSet();
        var recipeIds = batches.Select(b => b.MasterRecipeId).ToHashSet();
        var versionIds = batches.Select(b => b.RecipeVersionId).ToHashSet();
        var equipment = await _db.Equipment.AsNoTracking()
            .Where(e => equipmentIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var recipes = await _db.Recipes.AsNoTracking()
            .Where(r => recipeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var versions = await _db.RecipeVersions.AsNoTracking()
            .Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);

        return batches.Select(b =>
        {
            equipment.TryGetValue(b.EquipmentId, out var eq);
            recipes.TryGetValue(b.MasterRecipeId, out var recipe);
            versions.TryGetValue(b.RecipeVersionId, out var version);
            var snapshot = Deserialize(b.ControlRecipeJson);
            return new BatchListItemDto(
                b.Id, b.BatchNo, snapshot?.RecipeName ?? recipe?.Name ?? "",
                snapshot?.VersionNumber ?? version?.VersionNumber ?? 0,
                eq?.Code ?? "", b.ProductName, b.Status, b.HandshakePhase, b.CurrentStepIndex,
                b.CreatedAt, b.StartedAt);
        }).ToList();
    }

    public async Task<BatchDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var batch = await LoadAsync(id, ct);
        var eq = await _db.Equipment.AsNoTracking().FirstAsync(e => e.Id == batch.EquipmentId, ct);
        var snapshot = Deserialize(batch.ControlRecipeJson)
                       ?? throw new DomainException("SNAPSHOT", "控制配方快照损坏。");
        var integrity = SnapshotIntegrity.Verify(snapshot, JsonOptions);
        var boundIds = UnitEquipmentBinding.AllIds(snapshot, batch.EquipmentId);
        var equipmentRows = await _db.Equipment.AsNoTracking()
            .Where(e => boundIds.Contains(e.Id))
            .ToListAsync(ct);
        var events = await _db.HandshakeEvents.AsNoTracking()
            .Where(e => e.BatchId == batch.Id)
            .ToListAsync(ct);
        var lanes = BatchLanes.Build(
                snapshot,
                batch.EquipmentId,
                batch.StepExecutions,
                events,
                equipmentRows.ToDictionary(e => e.Id, e => e.Code),
                batch.HandshakePhase)
            .Select(l => new LaneHandshakeDto(
                l.EquipmentCode, l.EquipmentId, l.UnitProcedure, l.StepId, l.StepCode, l.Phase, l.Outcome))
            .ToList();
        return new BatchDetailDto(
            batch.Id, batch.BatchNo, batch.Status, batch.HandshakePhase, batch.FaultCode, batch.FaultMessage,
            eq.Id, eq.Name, snapshot, batch.CurrentStepId, batch.CurrentStepIndex,
            batch.StepExecutions.OrderBy(s => s.Ordinal).Select(s => new StepExecutionDto(
                s.StepId, s.StepCode, s.StepName, s.StepType, s.Ordinal, s.Outcome, s.StartedAt, s.CompletedAt, s.QualityJson)).ToList(),
            lanes,
            batch.CreatedAt, batch.StartedAt, batch.CompletedAt, integrity, MapWritePlan(snapshot),
            batch.ReleasedBy, batch.ReleasedAt, batch.ReleaseComment);
    }

    public async Task<IReadOnlyList<SampleDto>> SamplesAsync(Guid id, CancellationToken ct)
    {
        return (await _db.ProcessSamples.AsNoTracking()
            .Where(s => s.BatchId == id)
            .ToListAsync(ct))
            .OrderBy(s => s.SampledAt)
            .Select(s => new SampleDto(s.SampledAt, s.Tag, s.Value, s.Unit, s.StepId))
            .ToList();
    }

    public async Task<BatchDetailDto> CreateAsync(CreateBatchRequest request, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.Operator, UserRole.Supervisor);
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
        SnapshotIntegrity.Seal(snapshot, JsonOptions, out var json);
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
        EnsureRole(UserRole.Admin, UserRole.Operator, UserRole.Supervisor);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        await AcquireEquipmentAsync(batch, ct);

        var retry = batch.Status == BatchStatus.Faulted;
        batch.Queue();
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, retry ? "batch.retry.esign" : "batch.start.esign", "ProductionBatch", batch.Id.ToString(), batch.BatchNo));
        await SaveBatchStateAsync(ct);
        await OccupancyRealtime.PublishAsync(_db, _publisher, batch.Id, ct);
        await _scheduler.EnqueueStartAsync(batch.Id, ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> AbortAsync(Guid id, string reason, string password, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.Operator, UserRole.Supervisor);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        batch.Abort(string.IsNullOrWhiteSpace(reason) ? "操作员中止" : reason);
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "batch.abort.esign", "ProductionBatch", batch.Id.ToString(), reason));
        await SaveBatchStateAsync(ct);
        await _leases.ReleaseAsync(batch.Id, ct);
        await OccupancyRealtime.PublishAsync(_db, _publisher, batch.Id, ct);
        await _scheduler.EnqueueAbortAsync(batch.Id, reason, ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> HoldAsync(Guid id, string reason, string password, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.Operator, UserRole.Supervisor);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        if (batch.Status == BatchStatus.Queued)
        {
            batch.Hold(reason);
            _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "batch.hold.esign", "ProductionBatch", batch.Id.ToString(), reason));
            await SaveBatchStateAsync(ct);
            await OccupancyRealtime.PublishAsync(_db, _publisher, batch.Id, ct);
        }
        else if (batch.Status == BatchStatus.Running)
        {
            _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "batch.hold.esign", "ProductionBatch", batch.Id.ToString(), reason));
            await SaveBatchStateAsync(ct);
            await _scheduler.EnqueueHoldAsync(batch.Id, string.IsNullOrWhiteSpace(reason) ? "操作员保持" : reason, ct);
        }
        else
            throw new DomainException("CANNOT_HOLD", $"批次状态 {batch.Status} 不能保持。");
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> ResumeAsync(Guid id, string password, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.Operator, UserRole.Supervisor, UserRole.Quality);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        await AcquireEquipmentAsync(batch, ct);
        batch.Resume();
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "batch.resume.esign", "ProductionBatch", batch.Id.ToString(), batch.BatchNo));
        await SaveBatchStateAsync(ct);
        await OccupancyRealtime.PublishAsync(_db, _publisher, batch.Id, ct);
        await _scheduler.EnqueueStartAsync(batch.Id, ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> SkipAsync(Guid id, string reason, string password, Guid? stepId, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.Supervisor);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        var skipReason = string.IsNullOrWhiteSpace(reason) ? "主管跳步" : reason.Trim();
        var snapshot = Deserialize(batch.ControlRecipeJson)
                       ?? throw new DomainException("SNAPSHOT", "控制配方快照损坏。");
        var target = ResolveSkipTarget(batch, snapshot, stepId);
        var exec = batch.StepExecutions.Single(s => s.StepId == target.StepId);

        if (batch.Status == BatchStatus.Running)
        {
            if (exec.Outcome is "Completed" or "Skipped")
                throw new DomainException("SKIP_DONE", "当前工步已完成，不能跳过。");
            if (exec.Outcome is not ("Running" or "AwaitingConfirm"))
                throw new DomainException("SKIP_UNSAFE", "只能跳过正在等待 PLC_Ready、等待或人工确认的工步，禁止跨单元误跳邻道。");
            var last = await _db.HandshakeEvents.AsNoTracking()
                .Where(e => e.BatchId == batch.Id && e.StepId == target.StepId)
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefaultAsync(ct);
            var equipmentId = UnitEquipmentBinding.Resolve(snapshot, target.UnitProcedure, batch.EquipmentId);
            var eq = await _db.Equipment.AsNoTracking().FirstAsync(e => e.Id == equipmentId, ct);
            var lanes = BatchLanes.Parse(batch.HandshakePhase);
            var phase = last?.Phase
                        ?? lanes.GetValueOrDefault(eq.Code)
                        ?? batch.HandshakePhase;
            if (!BatchLanes.IsSkipSafePhase(phase))
                throw new DomainException("SKIP_UNSAFE", "工步已进入写参或执行，禁止跳步盲写。请先保持，待 PLC_Ready / 等待 / 人工确认后再跳过。");
            _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "batch.skip.esign", "ProductionBatch", batch.Id.ToString(),
                $"{skipReason} step={target.Code}"));
            await SaveBatchStateAsync(ct);
            await _scheduler.EnqueueSkipAsync(batch.Id, skipReason, target.StepId, ct);
            return await GetAsync(id, ct);
        }

        if (batch.Status is not BatchStatus.Held and not BatchStatus.Faulted)
            throw new DomainException("CANNOT_SKIP", $"批次状态 {batch.Status} 不能跳步。");

        if (exec.Outcome is "Completed")
            throw new DomainException("SKIP_DONE", "当前工步已完成，不能跳过。");
        exec.MarkSkipped(skipReason);
        _db.HandshakeEvents.Add(new HandshakeEvent(batch.Id, target.StepId, target.Code, batch.HandshakePhase, "skip", skipReason, null));
        var finished = false;
        if (batch.StepExecutions.All(s => s.Outcome is "Completed" or "Skipped"))
        {
            batch.Complete(DateTimeOffset.UtcNow);
            finished = true;
        }
        else
        {
            var next = snapshot.Steps.FirstOrDefault(s =>
                batch.StepExecutions.Single(e => e.StepId == s.StepId).Outcome is "Pending" or "Running" or "Faulted" or "Held");
            if (next is not null)
                batch.AdvanceTo(next.StepId, snapshot.Steps.ToList().FindIndex(s => s.StepId == next.StepId));
            batch.Queue();
            await _scheduler.EnqueueStartAsync(batch.Id, ct);
        }
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "batch.skip.esign", "ProductionBatch", batch.Id.ToString(),
            $"{skipReason} step={target.Code}"));
        await SaveBatchStateAsync(ct);
        if (finished)
            await _leases.ReleaseAsync(batch.Id, ct);
        await OccupancyRealtime.PublishAsync(_db, _publisher, batch.Id, ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> ConfirmAsync(Guid id, string comment, string password, Guid? stepId, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.Operator, UserRole.Supervisor, UserRole.Quality);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        if (batch.Status != BatchStatus.Running)
            throw new DomainException("CANNOT_CONFIRM", "只有运行中的批次可以人工确认。");
        var snapshot = Deserialize(batch.ControlRecipeJson)
                       ?? throw new DomainException("SNAPSHOT", "控制配方快照损坏。");
        var awaiting = batch.StepExecutions
            .Where(e => e.Outcome is "AwaitingConfirm" or "Running")
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
        if (exec.Outcome is "Completed" or "Skipped")
            throw new DomainException("CONFIRM_DONE", "该人工确认工步已结束。");
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "batch.confirm.esign", "ProductionBatch", batch.Id.ToString(),
            string.IsNullOrWhiteSpace(comment) ? target.Code : comment));
        await SaveBatchStateAsync(ct);
        await _scheduler.EnqueueConfirmAsync(batch.Id, string.IsNullOrWhiteSpace(comment) ? "操作员确认" : comment.Trim(), target.StepId, ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> ReleaseAsync(Guid id, string comment, string password, CancellationToken ct)
    {
        EnsureRole(UserRole.Quality);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        var snapshot = Deserialize(batch.ControlRecipeJson)
                       ?? throw new DomainException("SNAPSHOT", "控制配方快照损坏。");
        var labs = await _db.LabSamples.AsNoTracking().Where(s => s.BatchId == batch.Id).ToListAsync(ct);
        if (QualityDisposition.HasPendingFinalSample(labs))
            throw new DomainException("LAB_PENDING", "终检样品尚未判定，不能放行。");
        if ((QualityDisposition.HasOutOfSpec(snapshot, batch.StepExecutions) || QualityDisposition.HasFailedLabSample(labs))
            && string.IsNullOrWhiteSpace(comment))
            throw new DomainException("QUALITY_OOS", "归档质检或实验室样品超差，偏差放行必须填写意见。");
        batch.Release(_user.UserName ?? "quality", comment, DateTimeOffset.UtcNow);
        await _lots.ApplyBatchDispositionAsync(batch, ct);
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName ?? "quality", "batch.release.esign", "ProductionBatch", batch.Id.ToString(),
            batch.ReleaseComment));
        await SaveBatchStateAsync(ct);
        await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "released", new { batch.BatchNo, status = batch.Status.ToString() }), ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> RejectDispositionAsync(Guid id, string reason, string password, CancellationToken ct)
    {
        EnsureRole(UserRole.Quality);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        batch.RejectDisposition(_user.UserName ?? "quality", reason, DateTimeOffset.UtcNow);
        await _lots.ApplyBatchDispositionAsync(batch, ct);
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName ?? "quality", "batch.reject.esign", "ProductionBatch", batch.Id.ToString(),
            batch.ReleaseComment));
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

        var running = batch.StepExecutions.FirstOrDefault(s => s.Outcome is "Running" or "AwaitingConfirm");
        if (running is not null)
            return snapshot.Steps.Single(s => s.StepId == running.StepId);

        var index = Math.Clamp(batch.CurrentStepIndex, 0, Math.Max(snapshot.Steps.Count - 1, 0));
        return snapshot.Steps[index];
    }

    public async Task<IReadOnlyList<HandshakeLogDto>> HandshakeLogAsync(Guid id, CancellationToken ct)
    {
        _ = await LoadAsync(id, ct);
        var rows = await _db.HandshakeEvents.AsNoTracking().Where(e => e.BatchId == id).ToListAsync(ct);
        return rows
            .OrderBy(e => e.CreatedAt)
            .Select(e => new HandshakeLogDto(e.CreatedAt, e.StepCode, e.Phase, e.Kind, e.Detail, e.RemainingSeconds))
            .ToList();
    }

    public async Task<IReadOnlyList<SnapshotDriftDto>> SnapshotDriftAsync(Guid id, CancellationToken ct)
    {
        var batch = await LoadAsync(id, ct);
        var snapshot = Deserialize(batch.ControlRecipeJson)
                       ?? throw new DomainException("SNAPSHOT", "控制配方快照损坏。");
        var recipe = await _db.Recipes
            .Include(r => r.Versions).ThenInclude(v => v.Steps).ThenInclude(s => s.Parameters)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == batch.MasterRecipeId, ct);
        var approved = recipe?.Versions.FirstOrDefault(v => v.Id == recipe.CurrentApprovedVersionId);
        var masterSteps = approved?.Steps.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase)
                          ?? new Dictionary<string, RecipeStep>(StringComparer.OrdinalIgnoreCase);

        var drifts = new List<SnapshotDriftDto>();
        foreach (var step in snapshot.Steps)
        {
            masterSteps.TryGetValue(step.Code, out var master);
            foreach (var parameter in step.Parameters)
            {
                var current = master?.Parameters.FirstOrDefault(p => p.SlotIndex == parameter.SlotIndex)?.Setpoint;
                drifts.Add(new SnapshotDriftDto(
                    step.Code,
                    parameter.Name,
                    parameter.Setpoint,
                    current,
                    current is double value && Math.Abs(value - parameter.Setpoint) > 1e-9));
            }
        }

        return drifts;
    }

    public async Task<BatchRecordDto> RecordAsync(Guid id, CancellationToken ct)
    {
        var detail = await GetAsync(id, ct);
        var handshake = await HandshakeLogAsync(id, ct);
        var samples = await SamplesAsync(id, ct);
        var drift = await SnapshotDriftAsync(id, ct);
        var version = await _db.RecipeVersions
            .Include(v => v.Approvals)
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == detail.Snapshot.RecipeVersionId, ct);
        var approvals = version is null
            ? (IReadOnlyList<ApprovalDto>)[]
            : version.Approvals.OrderBy(a => a.Level).Select(RecipeService.MapApproval).ToList();
        var alarms = await AlarmsAsync(id, ct);
        var materials = await _lots.UsesForBatchAsync(id, ct);
        var labs = await _lots.SamplesForBatchAsync(id, ct);
        return new BatchRecordDto(
            detail.Id, detail.BatchNo, detail.Status, detail.SnapshotIntegrity, detail.Snapshot,
            detail.StepExecutions, handshake, samples, drift, approvals, alarms, DateTimeOffset.UtcNow,
            detail.WritePlan, detail.ReleasedBy, detail.ReleasedAt, detail.ReleaseComment, materials, labs);
    }

    public async Task<byte[]> ExportPdfAsync(Guid id, CancellationToken ct)
    {
        var record = await RecordAsync(id, ct);
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "batch.record.pdf", "ProductionBatch", id.ToString(),
            record.BatchNo));
        await _db.SaveChangesAsync(ct);
        return _pdf.Render(record);
    }

    public async Task<IReadOnlyList<ProcessAlarmDto>> AlarmsAsync(Guid? batchId, CancellationToken ct)
    {
        IQueryable<ProcessAlarm> query = _db.ProcessAlarms.AsNoTracking();
        if (batchId is Guid id)
            query = query.Where(a => a.BatchId == id);
        if (_db.SupportsServerDateOrdering)
            return await query.OrderByDescending(a => a.RaisedAt).Take(300)
                .Select(MapAlarmQuery).ToListAsync(ct);
        var rows = await query.ToListAsync(ct);
        return rows
            .OrderByDescending(a => a.RaisedAt)
            .Take(300)
            .Select(MapAlarm)
            .ToList();
    }

    private static Expression<Func<ProcessAlarm, ProcessAlarmDto>> MapAlarmQuery =
        a => new ProcessAlarmDto(a.Id, a.BatchId, a.BatchNo, a.StepCode, a.Code, a.Severity, a.Message, a.RaisedAt, a.AcknowledgedAt, a.AcknowledgedBy);

    public async Task<ProcessAlarmDto> AcknowledgeAlarmAsync(Guid alarmId, CancellationToken ct)
    {
        EnsureRole(UserRole.Admin, UserRole.Operator, UserRole.Supervisor, UserRole.Quality);
        var alarm = await _db.ProcessAlarms.FirstOrDefaultAsync(a => a.Id == alarmId, ct)
                    ?? throw new DomainException("NOT_FOUND", "报警不存在。");
        alarm.Acknowledge(_user.DisplayName, DateTimeOffset.UtcNow);
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "alarm.ack", "ProcessAlarm", alarm.Id.ToString(),
            alarm.Code));
        await _db.SaveChangesAsync(ct);
        var dto = MapAlarm(alarm);
        await _publisher.PublishAsync(new ExecutionEvent(alarm.BatchId, "alarm", dto), ct);
        return dto;
    }

    private static IReadOnlyList<PlcWritePlanDto> MapWritePlan(ControlRecipeSnapshot snapshot) =>
        ControlRecipeWritePlan.FromSnapshot(snapshot).Select(item => new PlcWritePlanDto(
            item.StepId, item.StepCode, item.StepName, item.StepType,
            item.PlcStepId, item.PlcStepType, item.Parameters, item.WriteToPlc, item.Policy)).ToList();

    private static ProcessAlarmDto MapAlarm(ProcessAlarm a) =>
        new(a.Id, a.BatchId, a.BatchNo, a.StepCode, a.Code, a.Severity, a.Message, a.RaisedAt, a.AcknowledgedAt, a.AcknowledgedBy);

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
            c => (IReadOnlySet<StepType>)c.AllowedProcessTypes(),
            StringComparer.OrdinalIgnoreCase);
        EquipmentClassRules.EnsureCompatible(
            snapshot.Steps.Select(s => (s.Code, s.Type, s.UnitProcedure)),
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
        var ids = BoundEquipmentIds(batch);
        var codes = await _db.Equipment
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Code, ct);
        await _leases.AcquireAsync(batch, ids, codes, ct);
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

    public static HashSet<Guid> BoundEquipmentIds(ProductionBatch batch)
    {
        var snapshot = Deserialize(batch.ControlRecipeJson);
        return UnitEquipmentBinding.AllIds(snapshot, batch.EquipmentId).ToHashSet();
    }

    private async Task<ProductionBatch> LoadAsync(Guid id, CancellationToken ct) =>
        await _db.Batches.Include(b => b.StepExecutions).FirstOrDefaultAsync(b => b.Id == id, ct)
        ?? throw new DomainException("NOT_FOUND", "批次不存在。");

    private Task RequireEsignAsync(string password, CancellationToken ct) => _esign.RequireAsync(password, ct);

    private void EnsureRole(params UserRole[] allowed) => _esign.EnsureRole(allowed);

    public static ControlRecipeSnapshot? Deserialize(string json) =>
        JsonSerializer.Deserialize<ControlRecipeSnapshot>(json, JsonOptions);
}
