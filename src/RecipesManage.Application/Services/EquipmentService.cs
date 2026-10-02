using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipesManage.Application.Contracts;
using RecipesManage.Application.Dtos;
using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Common;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Recipes;

namespace RecipesManage.Application.Services;

public sealed class EquipmentService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IPlcDriverFactory _drivers;
    private readonly ILogger<EquipmentService> _log;
    private readonly ISimulatorControl? _simulator;

    /// <param name="simulator">仿真项目没注册时为 null：此时只是不能注入故障，其余设备管理不受影响。</param>
    public EquipmentService(
        IAppDbContext db, ICurrentUser user, IPlcDriverFactory drivers, ILogger<EquipmentService> log,
        ISimulatorControl? simulator = null)
    {
        _db = db;
        _user = user;
        _drivers = drivers;
        _log = log;
        _simulator = simulator;
    }

    public async Task<IReadOnlyList<EquipmentDto>> ListAsync(CancellationToken ct)
    {
        var rows = await _db.Equipment.AsNoTracking().OrderBy(e => e.Code).ToListAsync(ct);
        var occ = await OccupancyIndexAsync(ct);
        return rows.Select(e => Overlay(Map(e), occ)).ToList();
    }

    public async Task<EquipmentDto> UpsertAsync(Guid? id, UpsertEquipmentRequest request, CancellationToken ct)
    {
        _user.EnsureCan(Capabilities.EquipmentAdmin, "仅管理员可配置设备与 PLC 点表。");

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
        _user.EnsureCan(Capabilities.EquipmentAdmin, "仅管理员可配置设备与 PLC 点表。");
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
        _user.EnsureCan(Capabilities.EquipmentSimulate, "仅车间操作员或工艺主管可注入仿真故障。");
        if (_simulator is null)
            throw new DomainException("NO_SIM_RUNTIME", "本次部署没有启用仿真模块，无法注入仿真故障。");

        var entity = await _db.Equipment.FirstOrDefaultAsync(e => e.Id == id, ct)
                     ?? throw new DomainException("NOT_FOUND", "设备不存在。");
        if (entity.Protocol != PlcProtocol.Simulator)
            throw new DomainException("NOT_SIM", "只能对 Simulator 注入握手故障，真实 PLC 禁止此操作。");

        var normalized = string.IsNullOrWhiteSpace(mode) ? "None" : mode.Trim();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "None", "HoldNotReady", "NoAck", "StepError", "DropHeartbeat", "CorruptEcho" };
        if (!allowed.Contains(normalized))
            throw new DomainException("FAULT_MODE", "故障模式无效。");

        // 先落审计，再动仿真器：保存失败就等于什么都没发生；反过来先注入再保存，
        // 一旦库写不进去就会留下一次现场状态改变而履历里查不到（最坏的那种不对称）。
        _db.AuditLogs.Add(new AuditLog(_user.UserId, _user.UserName, "equipment.inject-fault", "EquipmentLine", entity.Id.ToString(), normalized));
        await _db.SaveChangesAsync(ct);
        _simulator.InjectFault(entity.Id, normalized);
        var message = normalized.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? "已清除仿真故障，PLC_Ready 恢复。"
            : $"已注入 {normalized}：上位机必须停在当前握手阶段，禁止盲写下一步。";
        return new TagMapCheckDto("Ok", message);
    }

    public async Task<ConnectionTestDto> TestConnectionAsync(Guid id, CancellationToken ct)
    {
        _user.EnsureCan(Capabilities.EquipmentOperate);
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
            // 不把 ex.Message 原样回给客户端：连接失败的消息里常带主机、端口、驱动内部栈摘要，
            // 那是给攻击者看的现场拓扑。诊断信息进日志，界面只拿到可行动的一句话。
            _log.LogWarning(ex, "设备 {EquipmentId} 连接测试失败", entity.Id);
            return new ConnectionTestDto(false, false, sw.Elapsed.TotalMilliseconds, entity.Protocol.ToString(),
                $"连接失败：{DescribeConnectionFailure(ex)}");
        }
    }

    /// <summary>把驱动异常收敛成"能看懂但不出细节"的一句话。</summary>
    private static string DescribeConnectionFailure(Exception ex) => ex switch
    {
        OperationCanceledException => "8 秒内未响应，请检查设备是否上电、地址与端口是否正确。",
        TimeoutException => "8 秒内未响应，请检查设备是否上电、地址与端口是否正确。",
        System.Net.Sockets.SocketException => "网络不可达或被拒绝，请检查主机与端口。",
        _ => "握手位读取失败，请检查点表与设备状态（详情见服务端日志）。"
    };

    public async Task<DashboardDto> DashboardAsync(CancellationToken ct)
    {
        var live = (await _db.Batches.AsNoTracking()
            .Where(b => b.Status == Domain.Batches.BatchStatus.Queued
                        || b.Status == Domain.Batches.BatchStatus.Running
                        || b.Status == Domain.Batches.BatchStatus.Held
                        || b.Status == Domain.Batches.BatchStatus.Faulted)
            .ToListAsync(ct))
            .OrderByDescending(b => b.StartedAt)
            .ToList();
        var equipment = await _db.Equipment.AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var items = live.Select(b =>
        {
            equipment.TryGetValue(b.EquipmentId, out var eq);
            var snapshot = SnapshotJson.Deserialize(b.ControlRecipeJson);
            return new BatchListItemDto(
                b.Id, b.BatchNo, snapshot?.RecipeName ?? "", snapshot?.VersionNumber ?? 0,
                eq?.Code ?? "", b.ProductName, b.Status, b.HandshakePhase, b.CurrentStepIndex,
                b.CreatedAt, b.StartedAt);
        }).ToList();

        var occupancy = OccupancyRealtime.Snapshot(
            equipment.Values, live, await _db.EquipmentLeases.AsNoTracking().ToListAsync(ct));

        // 三个配方类计数数的是**配方**，口径 = MasterRecipe.LifecycleStatus（有当前草稿看草稿，否则看生效版本），
        // 与配方列表 draftStatus / 审核台筛的完全同一条规则——之前各数各的：磁贴按"版本行 + 只看草稿指针"，
        // 列表还带"没有草稿就退回生效版本状态"那条回退，于是磁贴 1、点进去 2 条。
        // 规则只在 LifecycleStatus 里写一次；这里做 SQL 投影镜像，一趟查询拿三个数（而不是三个 COUNT 往返）。
        var recipeStatuses = await _db.Recipes.AsNoTracking()
            .Select(r => new
            {
                Draft = r.Versions.Where(v => v.Id == r.CurrentDraftVersionId)
                    .Select(v => (RecipeStatus?)v.Status).FirstOrDefault(),
                Approved = r.Versions.Where(v => v.Id == r.CurrentApprovedVersionId)
                    .Select(v => (RecipeStatus?)v.Status).FirstOrDefault()
            })
            .ToListAsync(ct);
        var lifecycle = recipeStatuses.Select(x => x.Draft ?? x.Approved).ToList();

        return new DashboardDto(
            await _db.Batches.CountAsync(b => b.Status == Domain.Batches.BatchStatus.Running, ct),
            await _db.Batches.CountAsync(b => b.Status == Domain.Batches.BatchStatus.Queued, ct),
            lifecycle.Count(s => s == RecipeStatus.Draft),
            lifecycle.Count(s => s == RecipeStatus.InReview),
            recipeStatuses.Count(x => x.Approved is not null),
            await _db.Batches.CountAsync(b => b.Status == Domain.Batches.BatchStatus.Faulted, ct),
            await _db.ProcessAlarms.CountAsync(a => a.AcknowledgedAt == null, ct),
            items,
            occupancy,
            await _db.Batches.CountAsync(b => b.Status == Domain.Batches.BatchStatus.Completed, ct),
            // 数批次而不是待判样品行：一个批次可能挂 2 个待判终样，磁贴写 2、点进去列表只有 1 条。
            // 谓词与 BatchService.ListAsync 的 onlyLabPending 共用 LabSampleQuery.PendingFinal。
            await _db.Batches.CountAsync(b => _db.LabSamples.PendingFinal().Any(s => s.BatchId == b.Id), ct),
            await _db.Batches.CountAsync(b => b.Status == Domain.Batches.BatchStatus.Held, ct));
    }

    private async Task<Dictionary<Guid, EquipmentOccupant>> OccupancyIndexAsync(CancellationToken ct)
    {
        var live = await _db.Batches.AsNoTracking()
            .Where(b => b.Status == BatchStatus.Queued
                        || b.Status == BatchStatus.Running
                        || b.Status == BatchStatus.Held
                        || b.Status == BatchStatus.Faulted)
            .ToListAsync(ct);
        return EquipmentOccupancy.Index(live, SnapshotJson.BoundEquipmentIds,
            await _db.EquipmentLeases.AsNoTracking().ToListAsync(ct));
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

    public async Task<IReadOnlyList<EquipmentClassDto>> ClassesAsync(CancellationToken ct)
    {
        var rows = await _db.EquipmentClasses.Include(c => c.Templates).AsNoTracking().ToListAsync(ct);
        return rows.OrderBy(c => c.Code).Select(MapClass).ToList();
    }

    public async Task<PhaseTemplateDto> UpsertTemplateAsync(
        Guid classId, Guid? templateId, UpsertPhaseTemplateRequest request, CancellationToken ct)
    {
        EnsureLibraryRole();
        var cls = await _db.EquipmentClasses.Include(c => c.Templates)
                      .FirstOrDefaultAsync(c => c.Id == classId, ct)
                  ?? throw new DomainException("NOT_FOUND", "设备类不存在。");
        var specs = Specs(request.Parameters);
        PhaseTemplate template;
        string action;
        if (templateId is Guid existing)
        {
            template = cls.RequireTemplate(existing);
            cls.UpdateTemplate(
                existing, request.Name, request.StepType, request.Operation, request.WatchdogSeconds, specs,
                request.PlcProgramId);
            action = "equipment.template.update";
        }
        else
        {
            var code = request.Code.Trim().ToUpperInvariant();
            if (await _db.PhaseTemplates.AnyAsync(t => t.Code == code, ct))
                throw new DomainException("DUP_CODE", $"相模板编码 {code} 已存在。");
            template = cls.AddTemplate(
                request.Code, request.Name, request.StepType, request.Operation, request.WatchdogSeconds, specs,
                request.PlcProgramId);
            action = "equipment.template.create";
        }

        _db.AuditLogs.Add(new AuditLog(
            _user.UserId, _user.UserName, action, "PhaseTemplate", template.Id.ToString(),
            $"{cls.Code} {template.Code} 程序 {PlcProgram.Resolve(template.StepType, template.PlcProgramId)}"));
        await _db.SaveChangesAsync(ct);
        return MapTemplate(cls.Code, template);
    }

    public async Task DeleteTemplateAsync(Guid classId, Guid templateId, CancellationToken ct)
    {
        EnsureLibraryRole();
        var cls = await _db.EquipmentClasses.Include(c => c.Templates)
                      .FirstOrDefaultAsync(c => c.Id == classId, ct)
                  ?? throw new DomainException("NOT_FOUND", "设备类不存在。");
        var template = cls.RequireTemplate(templateId);
        var detail = $"{cls.Code} {template.Code}";
        cls.RemoveTemplate(template);
        _db.PhaseTemplates.Remove(template);
        _db.AuditLogs.Add(new AuditLog(
            _user.UserId, _user.UserName, "equipment.template.delete", "PhaseTemplate", templateId.ToString(), detail));
        await _db.SaveChangesAsync(ct);
    }

    private void EnsureLibraryRole()
    {
        _user.EnsureCan(Capabilities.PhaseLibrary, "仅管理员或工艺工程师可维护设备类相库。");
    }

    private static List<PhaseParameterSpec> Specs(IReadOnlyList<PhaseParameterDto> rows) =>
        (rows ?? []).Select(p => new PhaseParameterSpec
        {
            SlotIndex = p.SlotIndex,
            Name = p.Name,
            EngineeringUnit = p.EngineeringUnit,
            Setpoint = p.Setpoint,
            Min = p.Min,
            Max = p.Max,
            WriteToPlc = p.WriteToPlc,
            ArchiveAsQuality = p.ArchiveAsQuality,
            ScaleWithBatch = p.ScaleWithBatch,
            Semantic = p.Semantic,
            MeasuredTag = p.MeasuredTag
        }).ToList();

    private static EquipmentClassDto MapClass(EquipmentClass c) =>
        new(c.Id, c.Code, c.Name, c.Description,
            c.Templates.OrderBy(t => t.Code).Select(t => MapTemplate(c.Code, t)).ToList());

    private static PhaseTemplateDto MapTemplate(string classCode, PhaseTemplate t) =>
        new(t.Id, t.Code, t.Name, t.StepType, t.Operation, t.WatchdogSeconds, classCode,
            t.Parameters().Select(p => new PhaseParameterDto(
                p.SlotIndex, p.Name, p.EngineeringUnit, p.Setpoint, p.Min, p.Max,
                p.WriteToPlc, p.ArchiveAsQuality, p.ScaleWithBatch, p.Semantic, p.MeasuredTag)).ToList(),
            t.PlcProgramId);

    private static EquipmentDto Map(EquipmentLine e) =>
        new(e.Id, e.Code, e.Name, e.Protocol, e.Host, e.Port, e.PlcModel, e.Rack, e.Slot, e.Enabled, e.TagMapJson, e.Description, e.WatchdogJson,
            EquipmentClassCode: e.EquipmentClassCode);
}
