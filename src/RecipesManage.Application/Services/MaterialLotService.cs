using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Materials;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Application.Services;

public sealed class MaterialLotService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly EsignGuard _esign;

    public MaterialLotService(IAppDbContext db, ICurrentUser user, EsignGuard esign)
    {
        _db = db;
        _user = user;
        _esign = esign;
    }

    /// <summary>
    /// 状态名次按前端 labels.ts 的 lotStatusOrder（Open → Quarantine → Released → Consumed → Void），
    /// 库里存的是枚举底序，直接 ORDER BY 会把 Consumed 排在 Quarantine 前面。
    /// 写成表达式复用给升/降两个分支，改次序只有一处。
    /// </summary>
    private static readonly Expression<Func<MaterialLot, int>> StatusRank = l =>
        l.Status == MaterialLotStatus.Open ? 0 :
        l.Status == MaterialLotStatus.Quarantine ? 1 :
        l.Status == MaterialLotStatus.Released ? 2 :
        l.Status == MaterialLotStatus.Consumed ? 3 : 4;

    /// <summary>
    /// 物料批列表：服务端排序分页 + 搜索。以前截 400 条，来料登记多了以后旧批号在界面上
    /// 直接消失（谱系追溯恰恰要查那些老批号）。
    /// </summary>
    public async Task<MaterialLotPageDto> ListAsync(
        int skip,
        int take,
        string? sort,
        string? dir,
        string? q,
        string? status,
        CancellationToken ct)
    {
        take = Math.Clamp(take <= 0 ? 50 : take, 1, 200);
        skip = Math.Max(0, skip);
        var query = _db.MaterialLots.AsNoTracking();
        var wanted = ParseStatuses(status);
        if (wanted.Count > 0) query = query.Where(l => wanted.Contains(l.Status));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(l =>
                EF.Functions.Like(l.LotNumber, like, "\\") || EF.Functions.Like(l.MaterialCode, like, "\\") ||
                EF.Functions.Like(l.MaterialName, like, "\\"));
        }

        var ascending = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
        var ordered = (sort?.ToLowerInvariant(), ascending) switch
        {
            ("lotnumber", true) => query.OrderBy(l => l.LotNumber).ThenBy(l => l.Id),
            ("lotnumber", false) => query.OrderByDescending(l => l.LotNumber).ThenBy(l => l.Id),
            ("materialcode", true) => query.OrderBy(l => l.MaterialCode).ThenBy(l => l.Id),
            ("materialcode", false) => query.OrderByDescending(l => l.MaterialCode).ThenBy(l => l.Id),
            ("materialname", true) => query.OrderBy(l => l.MaterialName).ThenBy(l => l.Id),
            ("materialname", false) => query.OrderByDescending(l => l.MaterialName).ThenBy(l => l.Id),
            ("quantity", true) => query.OrderBy(l => l.Quantity).ThenBy(l => l.Id),
            ("quantity", false) => query.OrderByDescending(l => l.Quantity).ThenBy(l => l.Id),
            ("status", true) => query.OrderBy(StatusRank).ThenBy(l => l.Id),
            ("status", false) => query.OrderByDescending(StatusRank).ThenBy(l => l.Id),
            ("source", true) => query.OrderBy(l => l.Source).ThenBy(l => l.Id),
            ("source", false) => query.OrderByDescending(l => l.Source).ThenBy(l => l.Id),
            (_, true) => query.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id),
            _ => query.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id),
        };

        var total = await query.CountAsync(ct);
        var rows = await ordered.Skip(skip).Take(take).ToListAsync(ct);
        return new MaterialLotPageDto(total, rows.Select(MapLot).ToList());
    }

    /// <summary>
    /// 状态筛选：逗号分隔多值（创建批次只要「可投料」的 Open/Released，列表页要看全部）。
    /// 非法值忽略而不是抛——筛选串由前端拼，脏值不该让列表 500。
    /// </summary>
    private static List<MaterialLotStatus> ParseStatuses(string? status) =>
        (status ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => Enum.TryParse<MaterialLotStatus>(s, true, out var parsed) ? (MaterialLotStatus?)parsed : null)
        .Where(s => s is not null)
        .Select(s => s!.Value)
        .ToList();

    public async Task<MaterialLotDto> CreateReceivedAsync(CreateMaterialLotRequest request, CancellationToken ct)
    {
        EnsureCan(Capabilities.LotReceive);
        if (await _db.MaterialLots.AnyAsync(l => l.LotNumber == request.LotNumber.Trim(), ct))
            throw new DomainException("DUP_LOT", "物料批次号已存在。");
        var lot = MaterialLot.Receive(request.LotNumber, request.MaterialCode, request.MaterialName, request.Quantity, request.Uom);
        _db.MaterialLots.Add(lot);
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "lot.receive", "MaterialLot", lot.Id.ToString(), lot.LotNumber));
        await _db.SaveChangesAsync(ct);
        return MapLot(lot);
    }

    public async Task<MaterialLotDto> SplitAsync(Guid id, SplitLotRequest request, CancellationToken ct)
    {
        EnsureCan(Capabilities.LotHandle);
        if (await _db.MaterialLots.AnyAsync(l => l.LotNumber == request.ChildLotNumber.Trim(), ct))
            throw new DomainException("DUP_LOT", "拆分后的批次号已存在。");
        var parent = await _db.MaterialLots.FirstOrDefaultAsync(l => l.Id == id, ct)
                     ?? throw new DomainException("NOT_FOUND", "物料批次不存在。");
        var child = parent.Split(request.ChildLotNumber, request.Quantity);
        _db.MaterialLots.Add(child);
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "lot.split", "MaterialLot", child.Id.ToString(),
            $"{parent.LotNumber}->{child.LotNumber}"));
        await _db.SaveChangesAsync(ct);
        return MapLot(child);
    }

    /// <summary>
    /// 谱系：祖先链 + 子树 + 相关批次的使用记录。
    ///
    /// 不整表加载：物料批随运行年份线性增长，以前"打开谱系页"就是每开一次把全部物料批读进内存。
    /// 现在祖先沿 ParentLotId 逐层取（一次一个节点），后代每层一条 IN 查询；
    /// 顺序语义（祖先远→近、后代子批按批号前序展开）仍由 <see cref="MaterialGenealogy"/> 决定。
    /// </summary>
    public async Task<LotGenealogyDto> GenealogyAsync(Guid id, CancellationToken ct)
    {
        var lot = await _db.MaterialLots.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct)
                  ?? throw new DomainException("NOT_FOUND", "物料批次不存在。");
        var ancestors = await AncestorsAsync(lot, ct);
        var descendants = await DescendantsAsync(lot.Id, ct);
        var relatedIds = new HashSet<Guid> { lot.Id };
        foreach (var row in ancestors.Concat(descendants))
            relatedIds.Add(row.Id);
        var uses = await UsesForLotsAsync(relatedIds, ct);
        return new LotGenealogyDto(MapLot(lot), ancestors.Select(MapLot).ToList(), descendants.Select(MapLot).ToList(), uses);
    }

    private async Task<IReadOnlyList<MaterialLot>> AncestorsAsync(MaterialLot lot, CancellationToken ct)
    {
        var byId = new Dictionary<Guid, MaterialLot>();
        var parentId = lot.ParentLotId;
        while (parentId is Guid pid && !byId.ContainsKey(pid))
        {
            var parent = await _db.MaterialLots.AsNoTracking().FirstOrDefaultAsync(l => l.Id == pid, ct);
            if (parent is null) break;
            byId[parent.Id] = parent;
            parentId = parent.ParentLotId;
        }
        return MaterialGenealogy.Ancestors(lot, byId);
    }

    private async Task<IReadOnlyList<MaterialLot>> DescendantsAsync(Guid rootId, CancellationToken ct)
    {
        var all = new List<MaterialLot>();
        var seen = new HashSet<Guid> { rootId };
        var frontier = new List<Guid> { rootId };
        while (frontier.Count > 0)
        {
            var children = await _db.MaterialLots.AsNoTracking()
                .Where(l => l.ParentLotId != null && frontier.Contains(l.ParentLotId.Value))
                .ToListAsync(ct);
            var fresh = children.Where(c => seen.Add(c.Id)).ToList();
            if (fresh.Count == 0) break;
            all.AddRange(fresh);
            frontier = fresh.Select(c => c.Id).ToList();
        }
        return MaterialGenealogy.Descendants(rootId, all);
    }

    public async Task BindSnapshotLotsAsync(ProductionBatch batch, string productCode, string productName, string? lotNumber, IReadOnlyList<Guid>? chargeLotIds, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(lotNumber))
        {
            var number = lotNumber.Trim();
            var produced = await _db.MaterialLots.FirstOrDefaultAsync(l => l.LotNumber == number, ct);
            if (produced is null)
            {
                produced = MaterialLot.Produce(number, productCode, productName, batch.Id, null, null);
                _db.MaterialLots.Add(produced);
            }
            else
                produced.BindProducedBatch(batch.Id);

            if (!await _db.BatchMaterialUses.AnyAsync(u => u.BatchId == batch.Id && u.MaterialLotId == produced.Id && u.Role == MaterialUseRole.Produced, ct))
                _db.BatchMaterialUses.Add(new BatchMaterialUse(batch.Id, produced.Id, MaterialUseRole.Produced));
        }

        if (chargeLotIds is { Count: > 0 })
        {
            // 一次取齐再逐条校验：以前每个投料批两次往返（取批 + 查重），大单投料时是 2N 次。
            var ids = chargeLotIds.Distinct().ToList();
            var charges = await _db.MaterialLots.AsNoTracking()
                .Where(l => ids.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, ct);
            var existing = (await _db.BatchMaterialUses.AsNoTracking()
                    .Where(u => u.BatchId == batch.Id && u.Role == MaterialUseRole.Charge && ids.Contains(u.MaterialLotId))
                    .Select(u => u.MaterialLotId)
                    .ToListAsync(ct))
                .ToHashSet();
            foreach (var lotId in ids)
            {
                if (!charges.TryGetValue(lotId, out var charge))
                    throw new DomainException("NOT_FOUND", "投料批次不存在。");
                if (charge.Status is not MaterialLotStatus.Open and not MaterialLotStatus.Released)
                    throw new DomainException("LOT_STATUS", $"投料批次 {charge.LotNumber} 状态 {charge.Status} 不能投料。");
                if (existing.Add(charge.Id))
                    _db.BatchMaterialUses.Add(new BatchMaterialUse(batch.Id, charge.Id, MaterialUseRole.Charge, charge.Quantity));
            }
        }
    }

    public async Task ApplyBatchDispositionAsync(ProductionBatch batch, CancellationToken ct)
    {
        var uses = await _db.BatchMaterialUses.Where(u => u.BatchId == batch.Id).ToListAsync(ct);
        var lotIds = uses.Select(u => u.MaterialLotId).Distinct().ToList();
        var lots = await _db.MaterialLots.Where(l => lotIds.Contains(l.Id)).ToListAsync(ct);
        var byId = lots.ToDictionary(l => l.Id);
        foreach (var use in uses)
        {
            if (!byId.TryGetValue(use.MaterialLotId, out var lot))
                continue;
            if (batch.Status == BatchStatus.Released)
            {
                if (use.Role == MaterialUseRole.Produced)
                    lot.MarkReleased();
                else if (use.Role == MaterialUseRole.Charge && lot.Status == MaterialLotStatus.Open)
                    lot.MarkConsumed();
            }
            else if (batch.Status == BatchStatus.DispositionRejected && use.Role == MaterialUseRole.Produced)
                lot.Quarantine();
        }
    }

    public async Task<IReadOnlyList<BatchMaterialUseDto>> UsesForBatchAsync(Guid batchId, CancellationToken ct)
    {
        var uses = await _db.BatchMaterialUses.AsNoTracking().Where(u => u.BatchId == batchId).ToListAsync(ct);
        return await MapUsesAsync(uses, ct);
    }

    public async Task<IReadOnlyList<LabSampleDto>> SamplesForBatchAsync(Guid batchId, CancellationToken ct)
    {
        var rows = await _db.LabSamples.AsNoTracking().Where(s => s.BatchId == batchId).ToListAsync(ct);
        var lotIds = rows.Select(s => s.MaterialLotId).OfType<Guid>().Distinct().ToList();
        var lots = await _db.MaterialLots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        var entityIds = rows.Select(s => s.Id.ToString()).ToList();
        var signatures = entityIds.Count == 0
            ? []
            : await _db.SignatureRecords.AsNoTracking()
                .Where(s => s.EntityType == "LabSample" &&
                            s.Action == "lab.sample.dispose.esign" &&
                            entityIds.Contains(s.EntityId))
                .OrderBy(s => s.SignedAt)
                .ToListAsync(ct);
        var signatureBySample = signatures
            .GroupBy(s => s.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        return rows.OrderBy(s => s.TakenAt).Select(s =>
        {
            signatureBySample.TryGetValue(s.Id.ToString(), out var signature);
            var lotNumber = s.MaterialLotId is Guid id && lots.TryGetValue(id, out var lot)
                ? lot.LotNumber
                : null;
            return MapSample(s, lotNumber, MapDispositionSignature(s, signature));
        }).ToList();
    }

    public async Task<LabSampleDto> CreateSampleAsync(Guid batchId, CreateLabSampleRequest request, CancellationToken ct)
    {
        EnsureCan(Capabilities.LotHandle);
        _ = await _db.Batches.FirstOrDefaultAsync(b => b.Id == batchId, ct)
            ?? throw new DomainException("NOT_FOUND", "批次不存在。");
        if (await _db.LabSamples.AnyAsync(s => s.SampleCode == request.SampleCode.Trim(), ct))
            throw new DomainException("DUP_SAMPLE", "样品编号已存在。");
        var materialLotId = request.MaterialLotId;
        if (materialLotId is null)
        {
            // 取样入口不只有批记录页（操作员在监控页取样）：未显式给物料批时默认绑本批产出批，
            // 否则 LIMS 样品会丢掉与产品批的谱系链接，看起来"取了个没有来源的样"。
            materialLotId = await _db.BatchMaterialUses.AsNoTracking()
                .Where(u => u.BatchId == batchId && u.Role == MaterialUseRole.Produced)
                .Select(u => (Guid?)u.MaterialLotId)
                .FirstOrDefaultAsync(ct);
        }
        else if (!await _db.MaterialLots.AnyAsync(l => l.Id == materialLotId, ct))
        {
            throw new DomainException("NOT_FOUND", "物料批次不存在。");
        }
        var sample = new LabSample(
            request.SampleCode, batchId, request.SampleType, _user.DisplayName ?? _user.UserName, DateTimeOffset.UtcNow,
            materialLotId, request.ParentSampleId, request.StepId, request.ResultsJson);
        _db.LabSamples.Add(sample);
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "lab.sample.create", "LabSample", sample.Id.ToString(),
            $"batch={batchId} {sample.SampleCode}"));
        await _db.SaveChangesAsync(ct);
        return MapSample(sample, null);
    }

    public async Task<LabSampleDto> DisposeSampleAsync(Guid sampleId, LabSampleDispositionRequest request, CancellationToken ct, Guid? expectedBatchId = null)
    {
        EnsureCan(Capabilities.QualityDisposition);
        await RequireEsignAsync(request.Password, ct);
        var sample = await _db.LabSamples.FirstOrDefaultAsync(s => s.Id == sampleId, ct)
                     ?? throw new DomainException("NOT_FOUND", "样品不存在。");
        if (expectedBatchId is Guid batchId && sample.BatchId != batchId)
            throw new DomainException("NOT_FOUND", "样品不属于该生产批次。");
        sample.RecordDisposition(request.Disposition, _user.DisplayName ?? "quality", request.Comment, DateTimeOffset.UtcNow);
        _esign.Record(
            "lab.sample.dispose.esign", "LabSample", sample.Id.ToString(),
            ElectronicSignature.Batch("lab.sample.dispose.esign"),
            $"batch={sample.BatchId} {sample.SampleCode}:{sample.Disposition}",
            contentHashVersion: LabSampleSignatureContent.CurrentVersion,
            contentHash: LabSampleSignatureContent.ComputeHash(sample));
        await _db.SaveChangesAsync(ct);
        var signature = await _db.SignatureRecords.AsNoTracking()
            .Where(s => s.EntityType == "LabSample" && s.EntityId == sample.Id.ToString() &&
                        s.Action == "lab.sample.dispose.esign")
            .OrderByDescending(s => s.SignedAt)
            .FirstOrDefaultAsync(ct);
        return MapSample(sample, null, MapDispositionSignature(sample, signature));
    }

    public Task<int> PendingFinalCountAsync(CancellationToken ct) =>
        _db.LabSamples.CountAsync(s => s.SampleType == LabSampleType.Final && s.Disposition == LabSampleDisposition.Pending, ct);

    private async Task<IReadOnlyList<BatchMaterialUseDto>> UsesForLotsAsync(HashSet<Guid> lotIds, CancellationToken ct)
    {
        var uses = await _db.BatchMaterialUses.AsNoTracking().Where(u => lotIds.Contains(u.MaterialLotId)).ToListAsync(ct);
        return await MapUsesAsync(uses, ct);
    }

    private async Task<IReadOnlyList<BatchMaterialUseDto>> MapUsesAsync(List<BatchMaterialUse> uses, CancellationToken ct)
    {
        if (uses.Count == 0)
            return [];
        var lotIds = uses.Select(u => u.MaterialLotId).Distinct().ToList();
        var batchIds = uses.Select(u => u.BatchId).Distinct().ToList();
        var lots = await _db.MaterialLots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        var batches = await _db.Batches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        return uses.Select(u =>
        {
            lots.TryGetValue(u.MaterialLotId, out var lot);
            batches.TryGetValue(u.BatchId, out var batch);
            return new BatchMaterialUseDto(
                u.Id, u.BatchId, batch?.BatchNo, u.MaterialLotId, lot?.LotNumber ?? "", lot?.MaterialCode ?? "",
                u.Role, u.Quantity, u.StepId);
        }).ToList();
    }

    private static MaterialLotDto MapLot(MaterialLot lot) =>
        new(lot.Id, lot.LotNumber, lot.MaterialCode, lot.MaterialName, lot.ParentLotId, lot.Source, lot.Status,
            lot.Quantity, lot.Uom, lot.ProducedBatchId, lot.CreatedAt);

    private static LabSampleDispositionSignatureDto? MapDispositionSignature(
        LabSample sample, SignatureRecord? signature)
    {
        if (signature is null)
            return null;

        var integrity = signature.ContentHashVersion is null && signature.ContentHash is null
            ? "Unbound"
            : signature.ContentHashVersion is not int version || string.IsNullOrWhiteSpace(signature.ContentHash)
                ? "Mismatch"
                : version != LabSampleSignatureContent.CurrentVersion
                    ? "Unsupported"
                    : LabSampleSignatureContent.Matches(sample, version, signature.ContentHash)
                        ? "Verified"
                        : "Mismatch";
        return new LabSampleDispositionSignatureDto(
            signature.SignerName, signature.SignedAt, signature.Meaning, signature.Detail,
            signature.ContentHashVersion, signature.ContentHash, integrity);
    }

    private static LabSampleDto MapSample(
        LabSample sample, string? lotNumber, LabSampleDispositionSignatureDto? dispositionSignature = null) =>
        new(sample.Id, sample.SampleCode, sample.BatchId, sample.MaterialLotId, lotNumber, sample.ParentSampleId,
            sample.StepId, sample.SampleType, sample.Disposition, sample.ResultsJson, sample.TakenBy, sample.TakenAt,
            sample.DispositionBy, sample.DisposedAt, sample.Comment, dispositionSignature);

    private async Task RequireEsignAsync(string password, CancellationToken ct) =>
        await _esign.RequireAsync(password, ct);

    private void EnsureCan(Capability capability) => _esign.EnsureCan(capability);
}
