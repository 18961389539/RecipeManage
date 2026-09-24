using System.Text.Encodings.Web;
using System.Text.Json;
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

    /// <summary>
    /// 批次列表：筛选、排序、分页全部在 SQL 里做，返回的 <c>Total</c> 是筛选后的全量行数。
    ///
    /// 为什么要改：以前是"整表读进内存 → 按创建时间排 → 截 200 条"，于是列表其实永远看不到
    /// 第 200 条之前的批次，而前端的排序也只在这 200 条里排——用户点"按状态排序"得到的是
    /// 最近 200 批的状态序，不是全部批次的状态序。这不是性能问题，是给出的结论不对。
    /// 配方名 / 设备码要能排序和搜索，所以用左连接把它们带进同一条查询（左连接是为了
    /// 设备或配方被删掉后批次仍然列得出来）。
    /// </summary>
    public async Task<BatchListPageDto> ListAsync(
        int skip,
        int take,
        string? sort,
        string? dir,
        string? q,
        string? status,
        bool onlyLabPending,
        CancellationToken ct)
    {
        take = Math.Clamp(take <= 0 ? 50 : take, 1, 200);
        skip = Math.Max(0, skip);
        var like = NormalizeLike(q);

        var joined =
            from b in _db.Batches.AsNoTracking()
            join e in _db.Equipment.AsNoTracking() on b.EquipmentId equals e.Id into eg
            from e in eg.DefaultIfEmpty()
            join r in _db.Recipes.AsNoTracking() on b.MasterRecipeId equals r.Id into rg
            from r in rg.DefaultIfEmpty()
            join v in _db.RecipeVersions.AsNoTracking() on b.RecipeVersionId equals v.Id into vg
            from v in vg.DefaultIfEmpty()
            // 状态列要按前端 labels.ts 的 batchStatusOrder 排（Held 跟着 Running 走，不是按枚举底序），
            // 而枚举在库里是 int。把名次算进投影一次，两个方向的分支就都只引用这一个列，
            // 不必各写一份 CASE，也不会出现"改了一边次序"。这套次序不能挪到 C# 里排——那样只能排当页。
            select new
            {
                Batch = b, Equipment = e, Recipe = r, Version = v,
                StatusRank =
                    b.Status == BatchStatus.Created ? 0 :
                    b.Status == BatchStatus.Queued ? 1 :
                    b.Status == BatchStatus.Running ? 2 :
                    b.Status == BatchStatus.Held ? 3 :
                    b.Status == BatchStatus.Completed ? 4 :
                    b.Status == BatchStatus.DispositionRejected ? 5 :
                    b.Status == BatchStatus.Released ? 6 :
                    b.Status == BatchStatus.Faulted ? 7 :
                    b.Status == BatchStatus.Aborted ? 8 : 9
            };

        if (like is not null)
        {
            // 覆盖的列要和界面上显示的一致，否则"搜得到"与"看得见"会说两套话。
            // 唯一对不上的是配方名：列表显示的是快照里冻结的名字（配方后来改名也以当时为准），
            // 而 SQL 只能按联表里主配方的当前名筛（快照是 JSON 文本，按它筛等于把工步名、参数名也搜进去）。
            // EF 只能翻译直接调用的 EF.Functions.Like：包一层自己的小方法就会整条查询翻译失败（实测踩过）。
            joined = joined.Where(x =>
                EF.Functions.Like(x.Batch.BatchNo, like, "\\") ||
                EF.Functions.Like(x.Batch.ProductName, like, "\\") ||
                (x.Equipment != null && EF.Functions.Like(x.Equipment.Code, like, "\\")) ||
                (x.Recipe != null && EF.Functions.Like(x.Recipe.Name, like, "\\")));
        }
        // 状态是枚举，非法值直接忽略而不是抛——筛选框由前端拼，脏值不该让列表 500。
        if (Enum.TryParse<BatchStatus>(status, true, out var parsed))
            joined = joined.Where(x => x.Batch.Status == parsed);
        if (onlyLabPending)
            // 相关子查询：待实验室终审的批次数在服务器上算，翻页后前端只能看到当页标记，拦不住筛选。
            joined = joined.Where(x => _db.LabSamples.Any(s =>
                s.BatchId == x.Batch.Id
                && s.SampleType == LabSampleType.Final
                && s.Disposition == LabSampleDisposition.Pending));

        var ascending = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
        // 每一支都带 Id 兜底次级键：同一秒内创建的批次很多，没有它翻页会重复或漏行。
        var ordered = (sort?.ToLowerInvariant(), ascending) switch
        {
            ("batchno", true) => joined.OrderBy(x => x.Batch.BatchNo).ThenBy(x => x.Batch.Id),
            ("batchno", false) => joined.OrderByDescending(x => x.Batch.BatchNo).ThenBy(x => x.Batch.Id),
            ("recipename", true) => joined.OrderBy(x => x.Recipe!.Name).ThenBy(x => x.Batch.Id),
            ("recipename", false) => joined.OrderByDescending(x => x.Recipe!.Name).ThenBy(x => x.Batch.Id),
            ("recipeversion", true) => joined.OrderBy(x => x.Version!.VersionNumber).ThenBy(x => x.Batch.Id),
            ("recipeversion", false) => joined.OrderByDescending(x => x.Version!.VersionNumber).ThenBy(x => x.Batch.Id),
            ("equipmentcode", true) => joined.OrderBy(x => x.Equipment!.Code).ThenBy(x => x.Batch.Id),
            ("equipmentcode", false) => joined.OrderByDescending(x => x.Equipment!.Code).ThenBy(x => x.Batch.Id),
            ("productname", true) => joined.OrderBy(x => x.Batch.ProductName).ThenBy(x => x.Batch.Id),
            ("productname", false) => joined.OrderByDescending(x => x.Batch.ProductName).ThenBy(x => x.Batch.Id),
            ("status", true) => joined.OrderBy(x => x.StatusRank).ThenBy(x => x.Batch.Id),
            ("status", false) => joined.OrderByDescending(x => x.StatusRank).ThenBy(x => x.Batch.Id),
            ("startedat", true) => joined.OrderBy(x => x.Batch.StartedAt).ThenBy(x => x.Batch.Id),
            ("startedat", false) => joined.OrderByDescending(x => x.Batch.StartedAt).ThenBy(x => x.Batch.Id),
            ("completedat", true) => joined.OrderBy(x => x.Batch.CompletedAt).ThenBy(x => x.Batch.Id),
            ("completedat", false) => joined.OrderByDescending(x => x.Batch.CompletedAt).ThenBy(x => x.Batch.Id),
            ("releasedat", true) => joined.OrderBy(x => x.Batch.ReleasedAt).ThenBy(x => x.Batch.Id),
            ("releasedat", false) => joined.OrderByDescending(x => x.Batch.ReleasedAt).ThenBy(x => x.Batch.Id),
            (_, true) => joined.OrderBy(x => x.Batch.CreatedAt).ThenBy(x => x.Batch.Id),
            _ => joined.OrderByDescending(x => x.Batch.CreatedAt).ThenBy(x => x.Batch.Id),
        };

        var total = await joined.CountAsync(ct);
        var page = await ordered.Skip(skip).Take(take).ToListAsync(ct);
        var batchIds = page.Select(x => x.Batch.Id).ToList();

        var pendingFinal = (await _db.LabSamples.AsNoTracking()
            .Where(s => s.SampleType == LabSampleType.Final && s.Disposition == LabSampleDisposition.Pending
                        && batchIds.Contains(s.BatchId))
            .Select(s => s.BatchId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        return new BatchListPageDto(total, page.Select(x =>
        {
            var b = x.Batch;
            var snapshot = Deserialize(b.ControlRecipeJson);
            return new BatchListItemDto(
                b.Id, b.BatchNo, snapshot?.RecipeName ?? x.Recipe?.Name ?? "",
                snapshot?.VersionNumber ?? x.Version?.VersionNumber ?? 0,
                x.Equipment?.Code ?? "", b.ProductName, b.Status, b.HandshakePhase, b.CurrentStepIndex,
                b.CreatedAt, b.StartedAt, pendingFinal.Contains(b.Id));
        }).ToList());
    }

    /// <summary>搜索词：空串视为不过滤；LIKE 的通配符要转义，否则用户输入 % 就等于"匹配所有"。</summary>
    private static string? NormalizeLike(string? q) =>
        string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";

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
        var persistedLanes = await _db.Lanes.AsNoTracking()
            .Where(l => l.BatchId == batch.Id)
            .ToListAsync(ct);
        var laneStates = persistedLanes.Count > 0
            ? BatchLanes.FromRows(persistedLanes)
            : BatchLanes.Build(
                snapshot,
                batch.EquipmentId,
                batch.StepExecutions,
                events,
                equipmentRows.ToDictionary(e => e.Id, e => e.Code));
        var lanes = laneStates
            .Select(l => new LaneHandshakeDto(
                l.EquipmentCode, l.EquipmentId, l.UnitProcedure, l.StepId, l.StepCode, l.Phase, l.Outcome))
            .ToList();
        var intents = await _db.SchedulerIntents.AsNoTracking()
            .Where(i => i.BatchId == batch.Id)
            .ToListAsync(ct);
        return new BatchDetailDto(
            batch.Id, batch.BatchNo, batch.Status, batch.HandshakePhase, batch.FaultCode, batch.FaultMessage,
            eq.Id, eq.Name, snapshot, batch.CurrentStepId, batch.CurrentStepIndex,
            batch.StepExecutions.OrderBy(s => s.Ordinal).Select(s => new StepExecutionDto(
                s.StepId, s.StepCode, s.StepName, s.StepType, s.Ordinal, s.Outcome, s.StartedAt, s.CompletedAt, s.QualityJson)).ToList(),
            lanes,
            batch.CreatedAt, batch.StartedAt, batch.CompletedAt, integrity, MapWritePlan(snapshot),
            batch.ReleasedBy, batch.ReleasedAt, batch.ReleaseComment,
            IntentReason(intents, SchedulerIntentKinds.Hold),
            IntentReason(intents, SchedulerIntentKinds.Skip),
            IntentReason(intents, SchedulerIntentKinds.Confirm));
    }

    /// <summary>归档件用的全量样本：不降采样、不设窗口（趋势图才抽稀）。</summary>
    private async Task<IReadOnlyList<SampleDto>> AllSamplesAsync(Guid id, CancellationToken ct) =>
        (await _db.ProcessSamples.AsNoTracking()
            .Where(s => s.BatchId == id)
            .OrderBy(s => s.SampledAt).ThenBy(s => s.Id)
            .ToListAsync(ct))
        .Select(MapSample).ToList();

    /// <summary>归档件用的全量报警。</summary>
    private async Task<IReadOnlyList<ProcessAlarmDto>> AllAlarmsAsync(Guid id, CancellationToken ct) =>
        (await _db.ProcessAlarms.AsNoTracking()
            .Where(a => a.BatchId == id)
            .OrderBy(a => a.RaisedAt).ThenBy(a => a.Id)
            .ToListAsync(ct))
        .Select(MapAlarm).ToList();

    /// <summary>
    /// 趋势样本：只读回最近 <see cref="SampleReadWindow"/> 行，再按测点等间隔抽稀到
    /// <paramref name="maxPoints"/> 以内返回。原始样本一行都不删——批记录要留到留存期，
    /// 抽稀只发生在读的一侧，且把 <c>Total</c> 与 <c>Step</c> 一起给前端，
    /// 让界面能写明"这是每 N 点取 1 的结果"，而不是让用户误以为看到的是全数据。
    /// </summary>
    public async Task<SampleSeriesDto> SamplesAsync(Guid id, int maxPoints, CancellationToken ct)
    {
        maxPoints = Math.Clamp(maxPoints <= 0 ? 1500 : maxPoints, 50, 5000);
        var total = await _db.ProcessSamples.AsNoTracking().CountAsync(s => s.BatchId == id, ct);
        if (total == 0) return new SampleSeriesDto([], 0, 0, 1, maxPoints);

        var window = Math.Min(total, SampleReadWindow);
        var rows = await _db.ProcessSamples.AsNoTracking()
            .Where(s => s.BatchId == id)
            .OrderByDescending(s => s.SampledAt).ThenByDescending(s => s.Id)
            .Take(window)
            .ToListAsync(ct);
        rows.Reverse();   // 取的是最近 window 行，翻回时间正序给画布

        // 抽稀按测点分组做，并且所有分组用同一个步长：全局抽稀会让某些测点在抽到的时刻上缺值，
        // 步长不一致则"每 N 点取 1"这句话对不同的线就不成立了。
        var byTag = rows.GroupBy(s => s.Tag).ToList();
        var longest = byTag.Max(g => g.Count());
        var step = Math.Max(1, (int)Math.Ceiling(longest / (double)maxPoints * byTag.Count));
        if (step <= 1)
            return new SampleSeriesDto(rows.Select(MapSample).ToList(), total, rows.Count, 1, maxPoints);

        var points = byTag.SelectMany(g => g.Where((_, i) => i % step == 0)).OrderBy(s => s.SampledAt).ToList();
        return new SampleSeriesDto(points.Select(MapSample).ToList(), total, rows.Count, step, maxPoints);
    }

    /// <summary>单次趋势查询最多读回的行数：再大的批次也只画得下几千个点，读更多只是把库拖空。</summary>
    private const int SampleReadWindow = 50_000;

    private static SampleDto MapSample(ProcessSample s) =>
        new(s.SampledAt, s.Tag, s.Value, s.Unit, s.StepId);

    public async Task<BatchDetailDto> CreateAsync(CreateBatchRequest request, CancellationToken ct)
    {
        EnsureRole(UserRole.Operator, UserRole.Supervisor);
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
        EnsureRole(UserRole.Operator, UserRole.Supervisor);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        var snapshot = Deserialize(batch.ControlRecipeJson)
                       ?? throw new DomainException("SNAPSHOT", "控制配方快照损坏。");
        SnapshotIntegrity.DemandSealed(SnapshotIntegrity.Verify(snapshot, JsonOptions));
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
        EnsureRole(UserRole.Operator, UserRole.Supervisor);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        batch.Abort(string.IsNullOrWhiteSpace(reason) ? "操作员中止" : reason);
        await ClearIntentsAsync(batch.Id, ct);
        AuditEsign("batch.abort.esign", batch.Id, reason);
        await SaveBatchStateAsync(ct);
        await _leases.ReleaseAsync(batch.Id, ct);
        await OccupancyRealtime.PublishAsync(_db, _publisher, batch.Id, ct);
        await _scheduler.EnqueueAbortAsync(batch.Id, reason, ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> HoldAsync(Guid id, string reason, string password, CancellationToken ct)
    {
        EnsureRole(UserRole.Operator, UserRole.Supervisor);
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
        EnsureRole(UserRole.Operator, UserRole.Supervisor);
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
        EnsureRole(UserRole.Supervisor);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        var skipReason = string.IsNullOrWhiteSpace(reason) ? "主管跳步" : reason.Trim();
        var snapshot = Deserialize(batch.ControlRecipeJson)
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
        EnsureRole(UserRole.Operator, UserRole.Supervisor, UserRole.Quality);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        if (batch.Status != BatchStatus.Running)
            throw new DomainException("CANNOT_CONFIRM", "只有运行中的批次可以人工确认。");
        var snapshot = Deserialize(batch.ControlRecipeJson)
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
        EnsureExactRole(UserRole.Quality);
        await RequireEsignAsync(password, ct);
        var batch = await LoadAsync(id, ct);
        var snapshot = Deserialize(batch.ControlRecipeJson)
                       ?? throw new DomainException("SNAPSHOT", "控制配方快照损坏。");
        SnapshotIntegrity.DemandSealed(SnapshotIntegrity.Verify(snapshot, JsonOptions));
        var labs = await _db.LabSamples.AsNoTracking().Where(s => s.BatchId == batch.Id).ToListAsync(ct);
        if (QualityDisposition.HasPendingFinalSample(labs))
            throw new DomainException("LAB_PENDING", "终检样品尚未判定，不能放行。");
        if ((QualityDisposition.HasOutOfSpec(snapshot, batch.StepExecutions) || QualityDisposition.HasFailedLabSample(labs))
            && string.IsNullOrWhiteSpace(comment))
            throw new DomainException("QUALITY_OOS", "归档质检或实验室样品超差，偏差放行必须填写意见。");
        batch.Release(_user.UserName ?? "quality", comment, DateTimeOffset.UtcNow);
        await _lots.ApplyBatchDispositionAsync(batch, ct);
        AuditEsign("batch.release.esign", batch.Id, batch.ReleaseComment, _user.UserName ?? "quality");
        await SaveBatchStateAsync(ct);
        await _publisher.PublishAsync(new ExecutionEvent(batch.Id, "released", new { batch.BatchNo, status = batch.Status.ToString() }), ct);
        return await GetAsync(id, ct);
    }

    public async Task<BatchDetailDto> RejectDispositionAsync(Guid id, string reason, string password, CancellationToken ct)
    {
        EnsureExactRole(UserRole.Quality);
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
        // 归档件用全量样本与全量报警：eBR 是法定记录，不能拿趋势图那套抽稀结果去签。
        var samples = await AllSamplesAsync(id, ct);
        var drift = await SnapshotDriftAsync(id, ct);
        var version = await _db.RecipeVersions
            .Include(v => v.Approvals)
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == detail.Snapshot.RecipeVersionId, ct);
        var approvals = version is null
            ? (IReadOnlyList<ApprovalDto>)[]
            : version.Approvals.OrderBy(a => a.Seq).Select(RecipeService.MapApproval).ToList();
        var alarms = await AllAlarmsAsync(id, ct);
        var materials = await _lots.UsesForBatchAsync(id, ct);
        var labs = await _lots.SamplesForBatchAsync(id, ct);
        var entityId = id.ToString();
        var esignRows = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "ProductionBatch" && a.EntityId == entityId)
            .ToListAsync(ct);
        var esigns = esignRows
            .Where(a => a.Action.EndsWith(".esign", StringComparison.Ordinal))
            .OrderBy(a => a.At)
            .Select(MapEsign)
            .ToList();
        return new BatchRecordDto(
            detail.Id, detail.BatchNo, detail.Status, detail.SnapshotIntegrity, detail.Snapshot,
            detail.StepExecutions, handshake, samples, drift, approvals, alarms, DateTimeOffset.UtcNow,
            detail.WritePlan, detail.ReleasedBy, detail.ReleasedAt, detail.ReleaseComment, materials, labs, esigns);
    }

    public async Task<byte[]> ExportPdfAsync(Guid id, CancellationToken ct)
    {
        var record = await RecordAsync(id, ct);
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "batch.record.pdf", "ProductionBatch", id.ToString(),
            record.BatchNo));
        await _db.SaveChangesAsync(ct);
        return _pdf.Render(record);
    }

    /// <summary>
    /// 报警列表：同样是服务端排序分页。以前截 300 条，未确认的旧报警会被"最近 300 条"
    /// 挤出去，操作员在页面上看不到它们，也就永远不会去确认。
    /// </summary>
    public async Task<ProcessAlarmPageDto> AlarmsAsync(
        Guid? batchId,
        int skip,
        int take,
        string? sort,
        string? dir,
        string? q,
        bool onlyOpen,
        CancellationToken ct)
    {
        take = Math.Clamp(take <= 0 ? 50 : take, 1, 200);
        skip = Math.Max(0, skip);
        var like = NormalizeLike(q);
        var query = _db.ProcessAlarms.AsNoTracking();
        if (batchId is Guid id) query = query.Where(a => a.BatchId == id);
        if (onlyOpen) query = query.Where(a => a.AcknowledgedAt == null);
        if (like is not null)
            query = query.Where(a =>
                EF.Functions.Like(a.Code, like, "\\") || EF.Functions.Like(a.Message, like, "\\") ||
                EF.Functions.Like(a.StepCode, like, "\\") || EF.Functions.Like(a.BatchNo, like, "\\") ||
                EF.Functions.Like(a.Severity, like, "\\"));

        var ascending = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
        var ordered = (sort?.ToLowerInvariant(), ascending) switch
        {
            ("raisedat", true) => query.OrderBy(a => a.RaisedAt).ThenBy(a => a.Id),
            ("raisedat", false) => query.OrderByDescending(a => a.RaisedAt).ThenBy(a => a.Id),
            ("stepcode", true) => query.OrderBy(a => a.StepCode).ThenBy(a => a.Id),
            ("stepcode", false) => query.OrderByDescending(a => a.StepCode).ThenBy(a => a.Id),
            ("code", true) => query.OrderBy(a => a.Code).ThenBy(a => a.Id),
            ("code", false) => query.OrderByDescending(a => a.Code).ThenBy(a => a.Id),
            ("severity", true) => query.OrderBy(a => a.Severity).ThenBy(a => a.Id),
            ("severity", false) => query.OrderByDescending(a => a.Severity).ThenBy(a => a.Id),
            ("message", true) => query.OrderBy(a => a.Message).ThenBy(a => a.Id),
            ("message", false) => query.OrderByDescending(a => a.Message).ThenBy(a => a.Id),
            ("batchno", true) => query.OrderBy(a => a.BatchNo).ThenBy(a => a.Id),
            ("batchno", false) => query.OrderByDescending(a => a.BatchNo).ThenBy(a => a.Id),
            ("acknowledgedat", true) => query.OrderBy(a => a.AcknowledgedAt).ThenBy(a => a.Id),
            ("acknowledgedat", false) => query.OrderByDescending(a => a.AcknowledgedAt).ThenBy(a => a.Id),
            (_, true) => query.OrderBy(a => a.RaisedAt).ThenBy(a => a.Id),
            _ => query.OrderByDescending(a => a.RaisedAt).ThenBy(a => a.Id),
        };

        var total = await query.CountAsync(ct);
        var rows = await ordered.Skip(skip).Take(take).ToListAsync(ct);
        return new ProcessAlarmPageDto(total, rows.Select(MapAlarm).ToList());
    }

    public async Task<ProcessAlarmDto> AcknowledgeAlarmAsync(Guid alarmId, CancellationToken ct)
    {
        EnsureRole(UserRole.Operator, UserRole.Supervisor, UserRole.Quality);
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

    private void AuditEsign(string action, Guid batchId, string? extra, string? userName = null) =>
        _db.AuditLogs.Add(new AuditLog(
            _user.UserId,
            userName ?? _user.UserName,
            action,
            "ProductionBatch",
            batchId.ToString(),
            ElectronicSignature.AuditDetail(action, extra)));

    private static BatchEsignDto MapEsign(AuditLog log)
    {
        var meaning = ElectronicSignature.Batch(log.Action);
        var extra = log.Detail;
        if (!string.IsNullOrEmpty(extra) && extra.StartsWith(meaning, StringComparison.Ordinal))
            extra = extra[meaning.Length..].Trim();
        return new BatchEsignDto(log.Action, meaning, log.UserName, log.At, extra);
    }

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
        var ids = BoundEquipmentIds(batch);
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

            var declared = step.Parameters
                .Where(p => p.ArchiveAsQuality && !string.IsNullOrWhiteSpace(p.MeasuredTag))
                .Select(p => p.MeasuredTag)
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

    private static string? IntentReason(IEnumerable<SchedulerIntent> intents, string kind) =>
        intents.FirstOrDefault(i => i.Kind == kind)?.Reason;

    private async Task UpsertIntentAsync(Guid batchId, string kind, string reason, Guid? stepId, CancellationToken ct)
    {
        var row = await _db.SchedulerIntents.FirstOrDefaultAsync(i => i.BatchId == batchId && i.Kind == kind, ct);
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
    private void EnsureExactRole(params UserRole[] allowed) => _esign.EnsureExactRole(allowed);

    public static ControlRecipeSnapshot? Deserialize(string json) =>
        JsonSerializer.Deserialize<ControlRecipeSnapshot>(json, JsonOptions);
}
