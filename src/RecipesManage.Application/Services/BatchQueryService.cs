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
/// 批次的读路径：列表、详情、趋势、握手履历、快照漂移、电子批记录与 PDF、报警列表。
///
/// 为什么从 <see cref="BatchService"/> 拆出来：原来一个类 ~930 行，读与写缠在一起。
/// 读路径只依赖数据库、物料服务和 PDF 渲染器，不碰调度器、设备租约、密码校验——
/// 这些依赖留在写路径（<see cref="BatchService"/>），读这边因此可以独立测试、独立演进（缓存、只读副本……）。
/// 方向是单向的：写路径返回详情时委托给这里，这里不认识写路径。
///
/// 唯一的写入是导出 PDF 时留一行审计（"谁导出了这份批记录"），它不改任何业务状态。
/// </summary>
public sealed class BatchQueryService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly MaterialLotService _lots;
    private readonly IBatchRecordPdf _pdf;

    public BatchQueryService(IAppDbContext db, ICurrentUser user, MaterialLotService lots, IBatchRecordPdf pdf)
    {
        _db = db;
        _user = user;
        _lots = lots;
        _pdf = pdf;
    }

    /// <summary>读写两条路径共用的批次装载（含工步执行），保证"批次不存在"口径一致。</summary>
    internal async Task<ProductionBatch> LoadAsync(Guid id, CancellationToken ct) =>
        await _db.Batches.Include(b => b.StepExecutions).FirstOrDefaultAsync(b => b.Id == id, ct)
        ?? throw new DomainException("NOT_FOUND", "批次不存在。");
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
        var like = SearchLike.Normalize(q);

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
            // 谓词收在 LabSampleQuery.PendingFinal：总览磁贴与这里必须是同一批批次，
            // 否则磁贴写 3、点进去列表 2 条。相关子查询在服务器上算，翻页后前端只能看到当页标记，拦不住筛选。
            joined = joined.Where(x => _db.LabSamples.PendingFinal().Any(s => s.BatchId == x.Batch.Id));

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
            .BatchIdsPendingFinal(batchIds)
            .ToListAsync(ct)).ToHashSet();

        return new BatchListPageDto(total, page.Select(x =>
        {
            var b = x.Batch;
            var snapshot = SnapshotJson.Deserialize(b.ControlRecipeJson);
            return new BatchListItemDto(
                b.Id, b.BatchNo, snapshot?.RecipeName ?? x.Recipe?.Name ?? "",
                snapshot?.VersionNumber ?? x.Version?.VersionNumber ?? 0,
                x.Equipment?.Code ?? "", b.ProductName, b.Status, b.HandshakePhase, b.CurrentStepIndex,
                b.CreatedAt, b.StartedAt, pendingFinal.Contains(b.Id));
        }).ToList());
    }

    public async Task<BatchDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var batch = await LoadAsync(id, ct);
        var eq = await _db.Equipment.AsNoTracking().FirstAsync(e => e.Id == batch.EquipmentId, ct);
        var snapshot = SnapshotJson.Deserialize(batch.ControlRecipeJson)
                       ?? throw new DomainException("SNAPSHOT", "控制配方快照损坏。");
        var integrity = SnapshotIntegrity.Verify(snapshot, SnapshotJson.Options);
        var boundIds = UnitEquipmentBinding.AllIds(snapshot, batch.EquipmentId);
        var equipmentRows = await _db.Equipment.AsNoTracking()
            .Where(e => boundIds.Contains(e.Id))
            .ToListAsync(ct);
        var persistedLanes = await _db.Lanes.AsNoTracking()
            .Where(l => l.BatchId == batch.Id)
            .ToListAsync(ct);
        // 车道行的当前相位是真源；只有"从来没有车道行"的历史批次才需要按事件重建，
        // 那时也只取每个工步的最后一条，不再整批读回（长批次是几十万行）。
        var laneStates = persistedLanes.Count > 0
            ? BatchLanes.FromRows(persistedLanes)
            : BatchLanes.Build(
                snapshot,
                batch.EquipmentId,
                batch.StepExecutions,
                await LastEventPerStepAsync(batch.Id, ct),
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
    /// 趋势样本：覆盖<strong>整批</strong>，按测点在 SQL 侧等间隔取样，只回 ≤ <paramref name="maxPoints"/> 点/测点。
    ///
    /// 为什么不是"读最近 N 行再抽稀"：单设备一个班次就有 ~17 万行样本，按行数开窗等于把趋势截成
    /// 最近两三小时——而操作员看趋势要的恰恰是"这一批从头到尾的形状"。取样下推之后，
    /// 传输与物化的行数由 maxPoints 决定，跟批次跑了多久无关。
    ///
    /// 步长所有测点共用一个，否则"每 N 条取 1 点"这句话对图上不同的线成立得不一样。
    /// 原始样本一行不删：电子批记录仍走 <see cref="AllSamplesAsync"/> 的全量。
    /// </summary>
    public async Task<SampleSeriesDto> SamplesAsync(Guid id, int maxPoints, CancellationToken ct)
    {
        maxPoints = Math.Clamp(maxPoints <= 0 ? 1500 : maxPoints, 50, 5000);
        var sizes = await _db.ProcessSamples.AsNoTracking()
            .Where(s => s.BatchId == id)
            .GroupBy(s => s.Tag)
            .Select(g => new { Tag = g.Key, Rows = g.Count() })
            .ToListAsync(ct);
        var total = sizes.Sum(x => x.Rows);
        if (total == 0) return new SampleSeriesDto([], 0, 1, maxPoints);

        // 按最大的那个测点定步长：其余测点只会更稀，不会超上限。
        var step = Math.Max(1, (int)Math.Ceiling(sizes.Max(x => x.Rows) / (double)maxPoints));
        var rows = step == 1
            ? await _db.ProcessSamples.AsNoTracking()
                .Where(s => s.BatchId == id)
                .OrderBy(s => s.SampledAt).ThenBy(s => s.Id)
                .ToListAsync(ct)
            : await _db.ProcessSamples
                .FromSqlRaw(StridedSamplesSql, id, step)
                .OrderBy(s => s.SampledAt).ThenBy(s => s.Id)
                .ToListAsync(ct);

        return new SampleSeriesDto(rows.Select(MapSample).ToList(), total, step, maxPoints);
    }

    /// <summary>
    /// 每个测点各自按时间序编号，再按步长取点。BatchId 与步长都走占位符，不拼字符串。
    /// 外层必须再排一次：SQLite 不保证子查询里的 ORDER BY 会被保留下来。
    /// 列名写死在这里，改 <c>process_samples</c> 的模型要同步这条 SQL——
    /// <c>ListPagingTests.TrendSamplesAreDecimatedButStillReportTheRawSize</c> 会跑通整批覆盖，漏改会红。
    /// </summary>
    private const string StridedSamplesSql = """
        SELECT "Id", "BatchId", "StepId", "SampledAt", "Tag", "Value", "Unit", "CreatedAt", "UpdatedAt"
        FROM (
            SELECT "Id", "BatchId", "StepId", "SampledAt", "Tag", "Value", "Unit", "CreatedAt", "UpdatedAt",
                   ROW_NUMBER() OVER (PARTITION BY "Tag" ORDER BY "SampledAt", "Id") AS _rn
            FROM "process_samples"
            WHERE "BatchId" = {0}
        ) WHERE (_rn - 1) % {1} = 0
        """;

    private static SampleDto MapSample(ProcessSample s) =>
        new(s.SampledAt, s.Tag, s.Value, s.Unit, s.StepId);

    /// <summary>
    /// 握手履历：只回最近 <paramref name="take"/> 条（默认 <see cref="HandshakeLogWindow"/>）。
    ///
    /// 为什么必须开窗：这条是监控页每 4 秒拉的，而一个长跑批次的握手事件按 100ms 一拍累积，
    /// 旧写法整表读进内存再排序 —— 页面越到后面越慢，而且慢的是"看履历"这个动作本身。
    /// 完整履历仍能从电子批记录取（那条路径不截，见 <see cref="RecordAsync"/>）。
    /// </summary>
    public async Task<HandshakeLogPageDto> HandshakeLogAsync(Guid id, int take, CancellationToken ct)
    {
        _ = await LoadAsync(id, ct);
        take = Math.Clamp(take <= 0 ? HandshakeLogWindow : take, 1, 20_000);
        var total = await _db.HandshakeEvents.CountAsync(e => e.BatchId == id, ct);
        var rows = await _db.HandshakeEvents.AsNoTracking()
            .Where(e => e.BatchId == id)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Take(take)
            .ToListAsync(ct);
        rows.Reverse();   // 取的是最近 take 条，翻回时间正序给履历表
        return new HandshakeLogPageDto(total, rows.Select(MapHandshake).ToList());
    }

    /// <summary>
    /// 归档件用的全量握手履历：只有监控页开窗，电子批记录一行都不能少。
    /// 与上面分开写而不是"传个大点的 take"：take 有上限，靠调大上限来满足法务要求迟早会失守。
    /// </summary>
    private async Task<IReadOnlyList<HandshakeLogDto>> AllHandshakeLogAsync(Guid id, CancellationToken ct) =>
        (await _db.HandshakeEvents.AsNoTracking()
            .Where(e => e.BatchId == id)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .ToListAsync(ct))
        .Select(MapHandshake).ToList();

    private static HandshakeLogDto MapHandshake(HandshakeEvent e) =>
        new(e.CreatedAt, e.StepCode, e.Phase, e.Kind, e.Detail, e.RemainingSeconds);

    /// <summary>监控页履历表的默认条数：一屏滚得完，也够看完当前工步的每一次合法动作。</summary>
    private const int HandshakeLogWindow = 2_000;

    /// <summary>
    /// 每个工步的最后一条握手事件。
    ///
    /// 只为"从来没有车道行"的历史批次重建展示相位（见 <see cref="BatchLanes.Build"/>），
    /// 所以不能把整批事件读回来——那正是这次要消掉的那次全表读。
    /// </summary>
    private async Task<IReadOnlyList<HandshakeEvent>> LastEventPerStepAsync(Guid batchId, CancellationToken ct) =>
        await _db.HandshakeEvents.AsNoTracking()
            .Where(e => e.BatchId == batchId && e.StepId != null)
            .GroupBy(e => e.StepId!.Value)
            .Select(g => g.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).First())
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SnapshotDriftDto>> SnapshotDriftAsync(Guid id, CancellationToken ct)
    {
        var batch = await LoadAsync(id, ct);
        var snapshot = SnapshotJson.Deserialize(batch.ControlRecipeJson)
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
        // 批记录是归档凭据（含签名与检验数据）：第一道门是控制器策略，服务层再自保一层——
        // 导出 PDF 走的就是这个方法，堵在这里两条路径同时受控。
        _user.EnsureCan(Capabilities.BatchRecordView, "批记录仅主管、质量与管理员可查看。");
        var detail = await GetAsync(id, ct);
        var handshake = await AllHandshakeLogAsync(id, ct);
        // 归档件用全量样本与全量报警：eBR 是法定记录，不能拿趋势图那套抽稀结果去签。
        var samples = await AllSamplesAsync(id, ct);
        var drift = await SnapshotDriftAsync(id, ct);
        var version = await _db.RecipeVersions
            .Include(v => v.Approvals)
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == detail.Snapshot.RecipeVersionId, ct);
        var approvals = version is null
            ? (IReadOnlyList<ApprovalDto>)[]
            : version.Approvals.OrderBy(a => a.Seq).Select(RecipeSupport.MapApproval).ToList();
        var alarms = await AllAlarmsAsync(id, ct);
        var materials = await _lots.UsesForBatchAsync(id, ct);
        var labs = await _lots.SamplesForBatchAsync(id, ct);
        var entityId = id.ToString();
        var signatures = await _db.SignatureRecords.AsNoTracking()
                .Where(s => s.EntityType == "ProductionBatch" && s.EntityId == entityId)
                .OrderBy(s => s.SignedAt)
                .ThenBy(s => s.Id)
                .ToListAsync(ct);
        var record = new BatchRecordDto(
            detail.Id, detail.BatchNo, detail.Status, detail.SnapshotIntegrity, detail.Snapshot,
            detail.StepExecutions, handshake, samples, drift, approvals, alarms, DateTimeOffset.UtcNow,
            detail.WritePlan, detail.ReleasedBy, detail.ReleasedAt, detail.ReleaseComment, materials, labs, []);
        var evidenceHash = BatchRecordEvidenceHash.Compute(record);
        return record with
        {
            EvidenceHashVersion = BatchRecordEvidenceHash.CurrentVersion,
            EvidenceHash = evidenceHash,
            Esigns = signatures.Select(s => MapEsign(s, evidenceHash, detail.Snapshot)).ToList()
        };
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
        var like = SearchLike.Normalize(q);
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

    private static IReadOnlyList<PlcWritePlanDto> MapWritePlan(ControlRecipeSnapshot snapshot) =>
        ControlRecipeWritePlan.FromSnapshot(snapshot).Select(item => new PlcWritePlanDto(
            item.StepId, item.StepCode, item.StepName, item.StepType,
            item.PlcStepId, item.PlcStepType, item.Parameters, item.WriteToPlc, item.Policy)).ToList();

    internal static ProcessAlarmDto MapAlarm(ProcessAlarm a) =>
        new(a.Id, a.BatchId, a.BatchNo, a.StepCode, a.Code, a.Severity, a.Message, a.RaisedAt, a.AcknowledgedAt, a.AcknowledgedBy);

    private static BatchEsignDto MapEsign(
        SignatureRecord s, string currentEvidenceHash, ControlRecipeSnapshot snapshot)
    {
        var isDisposition = s.Action is "batch.release.esign" or "batch.reject.esign";
        var integrity = isDisposition
            ? BatchRecordEvidenceHash.Integrity(s.ContentHashVersion, s.ContentHash, currentEvidenceHash)
            : BatchActionSignatureContent.Supports(s.Action)
                ? BatchActionSignatureContent.Integrity(s, snapshot)
                : null;
        return new BatchEsignDto(
            s.Action, s.Meaning, s.SignerName, s.SignedAt, s.Detail,
            s.ContentHashVersion, s.ContentHash, integrity);
    }

    private static string? IntentReason(IEnumerable<SchedulerIntent> intents, string kind) =>
        intents.FirstOrDefault(i => i.Kind == kind)?.Reason;
}
