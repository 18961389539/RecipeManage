using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;

namespace RecipesManage.Application.Services;

public sealed class EquipmentService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IPlcDriverFactory _drivers;

    public EquipmentService(IAppDbContext db, ICurrentUser user, IPlcDriverFactory drivers)
    {
        _db = db;
        _user = user;
        _drivers = drivers;
    }

    public async Task<IReadOnlyList<EquipmentDto>> ListAsync(CancellationToken ct)
    {
        var rows = await _db.Equipment.AsNoTracking().OrderBy(e => e.Code).ToListAsync(ct);
        var occ = await OccupancyIndexAsync(ct);
        return rows.Select(e => Overlay(Map(e), occ)).ToList();
    }

    public async Task<EquipmentDto> UpsertAsync(Guid? id, UpsertEquipmentRequest request, CancellationToken ct)
    {
        var role = _user.Role ?? throw new DomainException("AUTH", "未登录。");
        if (role is not UserRole.Admin)
            throw new DomainException("FORBIDDEN", "仅管理员可配置设备与 PLC 点表。");

        TagMapValidator.Parse(request.TagMapJson);

        EquipmentLine entity;
        var action = "equipment.update";
        if (id is Guid existing)
        {
            entity = await _db.Equipment.FirstOrDefaultAsync(e => e.Id == existing, ct)
                     ?? throw new DomainException("NOT_FOUND", "设备不存在。");
            entity.Update(
                request.Name, request.Protocol, request.Host, request.Port, request.PlcModel,
                request.Rack, request.Slot, request.Enabled, request.TagMapJson, request.Description, request.WatchdogJson);
            entity.AssignClass(request.EquipmentClassCode);
        }
        else
        {
            if (await _db.Equipment.AnyAsync(e => e.Code == request.Code.Trim().ToUpperInvariant(), ct))
                throw new DomainException("DUP_CODE", "设备编码已存在。");
            entity = new EquipmentLine(
                request.Code, request.Name, request.Protocol, request.Host, request.Port,
                request.PlcModel, request.Rack, request.Slot, request.TagMapJson, request.Description, request.WatchdogJson);
            entity.AssignClass(request.EquipmentClassCode);
            _db.Equipment.Add(entity);
            action = "equipment.create";
        }

        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, action, "EquipmentLine", entity.Id.ToString(), entity.Code));
        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task<TagMapCheckDto> ValidateTagMapAsync(Guid id, CancellationToken ct)
    {
        var entity = await _db.Equipment.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct)
                     ?? throw new DomainException("NOT_FOUND", "设备不存在。");
        TagMapValidator.Parse(entity.TagMapJson);
        var hint = entity.Protocol == PlcProtocol.Simulator
            ? "仿真器点表结构合法，握手位齐全。"
            : "点表结构合法。真实 PLC 请在产线窗口期做通断确认，本接口不发起盲写。";
        return new TagMapCheckDto("Ok", hint);
    }

    public async Task<TagMapCheckDto> InjectSimulatorFaultAsync(Guid id, string mode, CancellationToken ct)
    {
        var role = _user.Role ?? throw new DomainException("AUTH", "未登录。");
        if (role is not UserRole.Admin and not UserRole.Operator and not UserRole.Supervisor)
            throw new DomainException("FORBIDDEN", "仅管理员或车间角色可注入仿真故障。");

        var entity = await _db.Equipment.FirstOrDefaultAsync(e => e.Id == id, ct)
                     ?? throw new DomainException("NOT_FOUND", "设备不存在。");
        if (entity.Protocol != PlcProtocol.Simulator)
            throw new DomainException("NOT_SIM", "只能对 Simulator 注入握手故障，真实 PLC 禁止此操作。");

        var normalized = string.IsNullOrWhiteSpace(mode) ? "None" : mode.Trim();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "None", "HoldNotReady", "NoAck", "StepError", "DropHeartbeat", "CorruptEcho" };
        if (!allowed.Contains(normalized))
            throw new DomainException("FAULT_MODE", "故障模式无效。");

        _drivers.InjectSimulatorFault(entity.Id, normalized);
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "equipment.inject-fault", "EquipmentLine", entity.Id.ToString(), normalized));
        await _db.SaveChangesAsync(ct);
        var message = normalized.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? "已清除仿真故障，PLC_Ready 恢复。"
            : $"已注入 {normalized}：上位机必须停在当前握手阶段，禁止盲写下一步。";
        return new TagMapCheckDto("Ok", message);
    }

    public async Task<ConnectionTestDto> TestConnectionAsync(Guid id, CancellationToken ct)
    {
        var entity = await _db.Equipment.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct)
                     ?? throw new DomainException("NOT_FOUND", "设备不存在。");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var plc = _drivers.Create(entity);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await plc.ConnectAsync(timeout.Token);
            var signals = await plc.ReadSignalsAsync(timeout.Token);
            var message = signals.PlcReady
                ? "已连接，PLC_Ready=1。本接口只读握手位，不写参数。"
                : "已连接，PLC 未 Ready。禁止写参，请待现场空闲。";
            return new ConnectionTestDto(true, signals.PlcReady, sw.Elapsed.TotalMilliseconds, entity.Protocol.ToString(), message);
        }
        catch (Exception ex)
        {
            return new ConnectionTestDto(false, false, sw.Elapsed.TotalMilliseconds, entity.Protocol.ToString(), ex.Message);
        }
    }

    public async Task<DashboardDto> DashboardAsync(CancellationToken ct)
    {
        var live = (await _db.Batches.AsNoTracking()
            .Where(b => b.Status == Domain.Batches.BatchStatus.Queued
                        || b.Status == Domain.Batches.BatchStatus.Running
                        || b.Status == Domain.Batches.BatchStatus.Held)
            .ToListAsync(ct))
            .OrderByDescending(b => b.StartedAt)
            .ToList();
        var equipment = await _db.Equipment.AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var items = live.Select(b =>
        {
            equipment.TryGetValue(b.EquipmentId, out var eq);
            var snapshot = BatchService.Deserialize(b.ControlRecipeJson);
            return new BatchListItemDto(
                b.Id, b.BatchNo, snapshot?.RecipeName ?? "", snapshot?.VersionNumber ?? 0,
                eq?.Code ?? "", b.ProductName, b.Status, b.HandshakePhase, b.CurrentStepIndex,
                b.CreatedAt, b.StartedAt);
        }).ToList();

        var occupancy = OccupancyRealtime.Snapshot(equipment.Values, live);

        return new DashboardDto(
            await _db.Batches.CountAsync(b => b.Status == Domain.Batches.BatchStatus.Running, ct),
            await _db.Batches.CountAsync(b => b.Status == Domain.Batches.BatchStatus.Queued, ct),
            await _db.RecipeVersions.CountAsync(v => v.Status == RecipeStatusDraft, ct),
            await _db.RecipeVersions.CountAsync(v => v.Status == RecipeStatusReview, ct),
            await _db.RecipeVersions.CountAsync(v => v.Status == RecipeStatusApproved, ct),
            await _db.Batches.CountAsync(b => b.Status == Domain.Batches.BatchStatus.Faulted, ct),
            await _db.ProcessAlarms.CountAsync(a => a.AcknowledgedAt == null, ct),
            items,
            occupancy,
            await _db.Batches.CountAsync(b => b.Status == Domain.Batches.BatchStatus.Completed, ct),
            await _db.LabSamples.CountAsync(s => s.SampleType == LabSampleType.Final && s.Disposition == LabSampleDisposition.Pending, ct));
    }

    private async Task<Dictionary<Guid, EquipmentOccupant>> OccupancyIndexAsync(CancellationToken ct)
    {
        var live = await _db.Batches.AsNoTracking()
            .Where(b => b.Status == BatchStatus.Queued || b.Status == BatchStatus.Running || b.Status == BatchStatus.Held)
            .ToListAsync(ct);
        return EquipmentOccupancy.Index(live, BatchService.BoundEquipmentIds);
    }

    private static EquipmentDto Overlay(EquipmentDto dto, IReadOnlyDictionary<Guid, EquipmentOccupant> occ)
    {
        if (!occ.TryGetValue(dto.Id, out var occupant))
            return dto;
        return dto with
        {
            Occupancy = "Occupied",
            OccupyingBatchNo = occupant.BatchNo,
            OccupyingBatchId = occupant.BatchId
        };
    }

    private static readonly Domain.Recipes.RecipeStatus RecipeStatusDraft = Domain.Recipes.RecipeStatus.Draft;
    private static readonly Domain.Recipes.RecipeStatus RecipeStatusReview = Domain.Recipes.RecipeStatus.InReview;
    private static readonly Domain.Recipes.RecipeStatus RecipeStatusApproved = Domain.Recipes.RecipeStatus.Approved;

    public async Task<IReadOnlyList<EquipmentClassDto>> ClassesAsync(CancellationToken ct)
    {
        var rows = await _db.EquipmentClasses.Include(c => c.Templates).AsNoTracking().ToListAsync(ct);
        return rows.OrderBy(c => c.Code).Select(c => new EquipmentClassDto(
            c.Id, c.Code, c.Name, c.Description,
            c.Templates.OrderBy(t => t.Code).Select(t => new PhaseTemplateDto(
                t.Id, t.Code, t.Name, t.StepType, t.Operation, t.WatchdogSeconds, c.Code,
                t.Parameters().Select(p => new PhaseParameterDto(
                    p.SlotIndex, p.Name, p.EngineeringUnit, p.Setpoint, p.Min, p.Max,
                    p.WriteToPlc, p.ArchiveAsQuality, p.ScaleWithBatch)).ToList())).ToList())).ToList();
    }

    private static EquipmentDto Map(EquipmentLine e) =>
        new(e.Id, e.Code, e.Name, e.Protocol, e.Host, e.Port, e.PlcModel, e.Rack, e.Slot, e.Enabled, e.TagMapJson, e.Description, e.WatchdogJson,
            EquipmentClassCode: e.EquipmentClassCode);
}
