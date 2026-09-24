using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Identity;
using RecipesManage.Domain.Materials;
using RecipesManage.Domain.Recipes;
using RecipesManage.Infrastructure.Plc;

namespace RecipesManage.Infrastructure.Persistence;

/// <summary>
/// 启动引导选项。
/// Demo 为 false 时只写入让系统可用的最小骨架（账号 / 主设备 / 首套配方 / 设备类库），
/// 不再写入仿真从站与示范配方这类演示资产。
/// </summary>
public sealed record SeedOptions(bool Demo, string? InitialPassword);

public static class DatabaseSeeder
{
    /// <summary>
    /// 让系统可用的最小数据集。幂等：以现有数据为准，不覆盖用户改过的东西。
    /// </summary>
    public static async Task SeedAsync(
        AppDbContext db,
        IPasswordHasher hasher,
        SeedOptions options,
        ILogger log,
        CancellationToken ct = default)
    {
        await SeedUsersAsync(db, hasher, options, log, ct);
        await SeedPrimaryEquipmentAsync(db, ct);
        await SeedPrimaryRecipeAsync(db, ct);
        await EnsurePhaseLibraryAsync(db, ct);

        if (options.Demo)
            await SeedDemoAssetsAsync(db, ct);
    }

    private static async Task SeedDemoAssetsAsync(AppDbContext db, CancellationToken ct)
    {
        await EnsureModbusLoopbackAsync(db, ct);
        await EnsureOpcUaLoopbackAsync(db, ct);
        await EnsureSiemensLoopbackAsync(db, ct);
        await EnsureParallelDemoAsync(db, ct);
        await EnsureConfirmAndOosDemoAsync(db, ct);
        await EnsureWaitAndProcessOpsDemoAsync(db, ct);
        await EnsureDemoLotsAsync(db, ct);
    }

    private static async Task SeedUsersAsync(
        AppDbContext db,
        IPasswordHasher hasher,
        SeedOptions options,
        ILogger log,
        CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct))
            return;

        var passwords = ResolveInitialPasswords(options, log);
        db.Users.AddRange(
            new AppUser("admin", "系统管理员", hasher.Hash(passwords[0]), UserRole.Admin),
            new AppUser("engineer", "工艺工程师", hasher.Hash(passwords[1]), UserRole.ProcessEngineer),
            new AppUser("supervisor", "工艺主管", hasher.Hash(passwords[2]), UserRole.Supervisor),
            new AppUser("qa", "质量工程师", hasher.Hash(passwords[3]), UserRole.Quality),
            new AppUser("operator", "车间操作员", hasher.Hash(passwords[4]), UserRole.Operator));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 演示环境沿用 README 里公开的示例口令；生产环境若未显式配置 Seed:AdminPassword，
    /// 则随机生成并只在启动日志里出现一次，避免"默认口令"成为生产后门。
    /// </summary>
    /// <summary>
    /// 五个初始口令。<strong>一个口令只能对应一个账号</strong>：
    /// 本系统的身份凭证就是口令本身（电子签名=再输一次登录密码），三审链要求
    /// 工程师提交 → 主管审 → 质量放行是"不同的人"。共用一份口令时一个人就能签完三级，
    /// 职责分离在数据层看不出来、在审计上直接失效。
    /// - Demo：沿用 README / e2e 公开的五个不同示例口令。
    /// - 非 Demo 且配了 Seed:AdminPassword：只给 admin，其余四个各自随机。
    /// - 非 Demo 且没配：五个全随机。
    /// 随机口令不落明文，只在首次建库时打印一次，那是安装者唯一能拿到它们的时刻。
    /// </summary>
    private static string[] ResolveInitialPasswords(SeedOptions options, ILogger log)
    {
        if (options.Demo)
        {
            if (!string.IsNullOrWhiteSpace(options.InitialPassword))
                log.LogWarning(
                    "Seed:Demo 为 true：初始口令使用公开的演示账号，Seed:AdminPassword 被忽略。" +
                    "演示环境请勿接入真实数据。");
            return ["Admin@123", "Engineer@123", "Supervisor@123", "Quality@123", "Operator@123"];
        }

        var names = new[] { "admin", "engineer", "supervisor", "qa", "operator" };
        var adminPassword = string.IsNullOrWhiteSpace(options.InitialPassword) ? null : options.InitialPassword.Trim();
        var passwords = names
            .Select((_, index) => index == 0 ? adminPassword ?? GeneratePassword() : GeneratePassword())
            .ToArray();

        for (var i = 0; i < names.Length; i++)
        {
            if (i == 0 && adminPassword is not null) continue;
            log.LogWarning(
                "初始账号 {User} 的随机口令 {Password} 只打印这一次，请首次登录后立即修改。",
                names[i], passwords[i]);
        }
        return passwords;
    }

    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#%^*-_";
        var bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        return "Brmes!" + new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    private static async Task SeedPrimaryEquipmentAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Equipment.AnyAsync(ct))
            return;

        var tagMap = JsonSerializer.Serialize(new HandshakeTagMap());
        db.Equipment.Add(new EquipmentLine(
            "HT-01", "1# 热处理炉", PlcProtocol.Simulator, "127.0.0.1", 102,
            "S7_1200", 0, 1, tagMap, "开发用进程内 PLC 仿真器，完整四步握手。"));
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedPrimaryRecipeAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Recipes.AnyAsync(ct))
            return;

        var engineer = await db.Users.SingleAsync(u => u.UserName == "engineer", ct);
        var supervisor = await db.Users.SingleAsync(u => u.UserName == "supervisor", ct);
        var qa = await db.Users.SingleAsync(u => u.UserName == "qa", ct);

        var recipe = MasterRecipe.Create("AL-HT-T6", "Al-6061-T6 热处理", "AL6061", "铝合金 6061 锻件",
            "固溶 + 时效的示范主配方，用于联调握手闭环。", engineer.Id);

        var draft = recipe.RequireDraft();
        var s1 = Heat("S10", "升温至固溶温度", StepType.Heat, 0, 80, 120, 180,
            P(0, "目标温度", "℃", 530, 520, 540, true, true),
            P(1, "升温斜率", "℃/min", 8, 4, 12, true, false),
            P(2, "升温时长", "s", 8, 0.5, 3600, false, false));
        var s2 = Heat("S20", "固溶保温", StepType.Hold, 1, 360, 120, 240,
            P(0, "保温温度", "℃", 530, 525, 535, true, true),
            P(1, "保温时长", "s", 8, 1, 3600, true, true));
        var s3 = Heat("S30", "淬火冷却", StepType.Cool, 2, 640, 120, 90,
            P(0, "终点温度", "℃", 40, 20, 60, true, true),
            P(1, "冷却时长", "s", 6, 1, 600, true, false));
        var s4 = Heat("S40", "时效保温", StepType.Hold, 3, 920, 120, 200,
            P(0, "时效温度", "℃", 175, 170, 180, true, true),
            P(1, "时效时长", "s", 8, 1, 7200, true, true));
        var s5 = Heat("S50", "出炉质检采样", StepType.QualityCheck, 4, 1200, 120, 60,
            P(0, "硬度下限", "HB", 95, 90, 110, false, true));

        draft.ReplaceProcedure(
            [s1, s2, s3, s4, s5],
            [
                new RecipeEdge(draft.Id, s1.Id, s2.Id),
                new RecipeEdge(draft.Id, s2.Id, s3.Id),
                new RecipeEdge(draft.Id, s3.Id, s4.Id),
                new RecipeEdge(draft.Id, s4.Id, s5.Id)
            ]);

        draft.Submit(DateTimeOffset.UtcNow.AddMinutes(-10), ApprovalChain.Standard, engineer.Id, engineer.DisplayName, "提交示范配方审核");
        draft.Decide(supervisor.Id, supervisor.DisplayName, ApprovalDecision.Approved, "工艺路径合理", DateTimeOffset.UtcNow.AddMinutes(-8));
        draft.Decide(qa.Id, qa.DisplayName, ApprovalDecision.Approved, "参数窗口可接受", DateTimeOffset.UtcNow.AddMinutes(-5));
        recipe.MarkApproved(draft);

        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(ct);

        RecipeStep Heat(string code, string name, StepType type, int ordinal, double x, double y, int watchdog, params RecipeParameter[] parameters) =>
            new(draft.Id, code, name, type, ordinal, x, y, watchdog, null, parameters, null,
                Isa88.DefaultUnitProcedure, Isa88.DefaultOperation(type), null, "FURNACE");

        static RecipeParameter P(int slot, string name, string unit, double sp, double min, double max, bool write, bool qc) =>
            new(slot, name, unit, sp, min, max, write, qc);
    }

    private static async Task EnsurePhaseLibraryAsync(AppDbContext db, CancellationToken ct)
    {
        if (!await db.EquipmentClasses.AnyAsync(ct))
        {
            static PhaseParameterSpec Spec(int slot, string name, string unit, double sp, double? min, double? max, bool write, bool qc, bool scale = false) =>
                new()
                {
                    SlotIndex = slot, Name = name, EngineeringUnit = unit, Setpoint = sp,
                    Min = min, Max = max, WriteToPlc = write, ArchiveAsQuality = qc, ScaleWithBatch = scale
                };

            var furnace = new EquipmentClass("FURNACE", "热处理炉", "允许 Heat / Hold / Cool 相写入 PLC。");
            furnace.AddTemplate("PH-HEAT", "升温至设定点", StepType.Heat, Isa88.DefaultOperation(StepType.Heat), 180,
            [
                Spec(0, "目标温度", "℃", 530, 520, 540, true, true),
                Spec(1, "升温斜率", "℃/min", 8, 4, 12, true, false),
                Spec(2, "升温时长", "s", 8, 0.5, 3600, false, false)
            ], (int)StepType.Heat);
            furnace.AddTemplate("PH-HOLD", "保温保持", StepType.Hold, Isa88.DefaultOperation(StepType.Hold), 240,
            [
                Spec(0, "保温温度", "℃", 530, 525, 535, true, true),
                Spec(1, "保温时长", "s", 8, 1, 3600, true, true)
            ], (int)StepType.Hold);
            furnace.AddTemplate("PH-COOL", "冷却至终点", StepType.Cool, Isa88.DefaultOperation(StepType.Cool), 120,
            [
                Spec(0, "终点温度", "℃", 40, 20, 60, true, true),
                Spec(1, "冷却时长", "s", 6, 1, 600, true, false)
            ], (int)StepType.Cool);

            var quench = new EquipmentClass("QUENCH", "淬火槽", "仅允许 Cool 相写入 PLC。");
            quench.AddTemplate("PH-QUENCH", "淬火冷却", StepType.Cool, Isa88.DefaultOperation(StepType.Cool), 90,
            [
                Spec(0, "终点温度", "℃", 40, 20, 60, true, true),
                Spec(1, "冷却时长", "s", 6, 1, 600, true, false)
            ], (int)StepType.Cool);

            var process = new EquipmentClass("PROCESS", "搅拌加压转移单元", "Mix / Pressure / Transfer，以及自定义程序号（冲洗、气缸）。");
            process.AddTemplate("PH-MIX", "搅拌混合", StepType.Mix, Isa88.DefaultOperation(StepType.Mix), 60,
            [
                Spec(0, "搅拌转速", "rpm", 60, 10, 200, true, false),
                Spec(1, "搅拌时长", "s", 8, 1, 600, true, true)
            ], (int)StepType.Mix);
            process.AddTemplate("PH-PRESS", "加压保压", StepType.Pressure, Isa88.DefaultOperation(StepType.Pressure), 60,
            [
                Spec(0, "目标压力", "bar", 2.5, 1, 6, true, true),
                Spec(1, "保压时长", "s", 6, 1, 600, true, false)
            ], (int)StepType.Pressure);
            process.AddTemplate("PH-XFER", "转移出料", StepType.Transfer, Isa88.DefaultOperation(StepType.Transfer), 60,
            [
                Spec(0, "转移量", "kg", 50, 1, 500, true, true, true),
                Spec(1, "转移时长", "s", 5, 1, 300, true, false)
            ], (int)StepType.Transfer);
            process.AddTemplate("PH-RINSE", "水冲洗", StepType.Transfer, "OP-Rinse 水冲洗", 30,
            [
                Spec(0, "冲洗流量", "L/min", 12, 1, 40, true, false),
                Spec(1, "冲洗时长", "s", 3, 0.5, 120, true, false)
            ], 21);
            process.AddTemplate("PH-CYL", "气缸保压", StepType.Pressure, "OP-Cyl 气缸保压", 30,
            [
                Spec(0, "气缸压力", "bar", 4, 1, 10, true, true),
                Spec(1, "保压时长", "s", 3, 0.5, 60, true, false)
            ], 22);

            var generic = new EquipmentClass("GENERIC", "通用环回站", "实验室环回允许全部写 PLC 相及自定义程序号。");
            foreach (var t in furnace.Templates.Concat(process.Templates).ToList())
                generic.AddTemplate("GEN-" + t.Code, t.Name, t.StepType, t.Operation, t.WatchdogSeconds, t.Parameters(), t.PlcProgramId);

            db.EquipmentClasses.AddRange(furnace, quench, process, generic);
            await db.SaveChangesAsync(ct);
        }

        async Task AssignAsync(string equipmentCode, string classCode)
        {
            var row = await db.Equipment.FirstOrDefaultAsync(e => e.Code == equipmentCode, ct);
            if (row is not null && string.IsNullOrWhiteSpace(row.EquipmentClassCode))
                row.AssignClass(classCode);
        }

        await AssignAsync("HT-01", "FURNACE");
        await AssignAsync("HT-02", "QUENCH");
        await AssignAsync("PR-01", "PROCESS");
        await AssignAsync("MB-01", "GENERIC");
        await AssignAsync("S7-01", "GENERIC");
        await AssignAsync("UA-01", "GENERIC");
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureDemoLotsAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.MaterialLots.AnyAsync(ct))
            return;
        db.MaterialLots.Add(MaterialLot.Receive("INGOT-DEMO-01", "AL6061", "铝合金锭", 250, "kg"));
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureModbusLoopbackAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Equipment.AnyAsync(e => e.Code == ModbusLoopbackHostedService.EquipmentCode, ct))
            return;
        var map = JsonSerializer.Serialize(HandshakeTagMap.ModbusLoopback());
        db.Equipment.Add(new EquipmentLine(
            ModbusLoopbackHostedService.EquipmentCode,
            "Modbus 环回从站",
            PlcProtocol.ModbusTcp,
            "127.0.0.1",
            ModbusLoopbackHostedService.DefaultPort,
            "MODBUS",
            0,
            1,
            map,
            "本机 IOTClient Modbus TCP 四步握手从站，用于协议栈联调。连接测试只读，禁止盲写。"));
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureOpcUaLoopbackAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Equipment.AnyAsync(e => e.Code == OpcUaLoopbackHostedService.EquipmentCode, ct))
            return;
        var map = JsonSerializer.Serialize(HandshakeTagMap.OpcUaLoopback());
        db.Equipment.Add(new EquipmentLine(
            OpcUaLoopbackHostedService.EquipmentCode,
            "OPC UA 环回从站",
            PlcProtocol.OpcUa,
            $"opc.tcp://127.0.0.1:{OpcUaLoopbackHostedService.DefaultPort}{OpcUaHandshakeSlave.PathSuffix}",
            OpcUaLoopbackHostedService.DefaultPort,
            "OPC_UA",
            0,
            1,
            map,
            "本机 OPC Foundation 四步握手从站，用于 OPC UA 协议栈联调。连接测试只读，禁止盲写。实验室自动接受自签证书。"));
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureSiemensLoopbackAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Equipment.AnyAsync(e => e.Code == SiemensS7LoopbackHostedService.EquipmentCode, ct))
            return;
        var map = JsonSerializer.Serialize(new HandshakeTagMap());
        db.Equipment.Add(new EquipmentLine(
            SiemensS7LoopbackHostedService.EquipmentCode,
            "S7 环回从站",
            PlcProtocol.SiemensS7,
            "127.0.0.1",
            SiemensS7LoopbackHostedService.DefaultPort,
            "S7_1200",
            0,
            1,
            map,
            "本机 IOTClient Siemens S7 ISO-on-TCP 四步握手从站，DB10 默认点表。连接测试只读，禁止盲写。"));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 第二套仿真炉 + 双 Unit Procedure 示范配方（固溶/淬火可并行，汇合质检）。
    /// 对已有库幂等：按设备编码与配方编码补齐。
    /// </summary>
    private static async Task EnsureParallelDemoAsync(AppDbContext db, CancellationToken ct)
    {
        var tagMap = JsonSerializer.Serialize(new HandshakeTagMap());
        if (!await db.Equipment.AnyAsync(e => e.Code == "HT-02", ct))
        {
            db.Equipment.Add(new EquipmentLine(
                "HT-02", "2# 淬火槽", PlcProtocol.Simulator, "127.0.0.1", 102,
                "S7_1200", 0, 1, tagMap, "第二套进程内 PLC 仿真器，用于 ISA-88 单元并行握手。"));
            await db.SaveChangesAsync(ct);
        }

        if (await db.Recipes.AnyAsync(r => r.Code == "AL-HT-2UP", ct))
            return;

        var engineer = await db.Users.SingleAsync(u => u.UserName == "engineer", ct);
        var supervisor = await db.Users.SingleAsync(u => u.UserName == "supervisor", ct);
        var qa = await db.Users.SingleAsync(u => u.UserName == "qa", ct);

        var recipe = MasterRecipe.Create("AL-HT-2UP", "Al-6061 双单元热处理", "AL6061", "铝合金 6061 锻件",
            "UP-固溶 与 UP-淬火 无互为前驱，可绑定不同 PLC 并行握手；完成后汇合 UP-QC。", engineer.Id);
        var draft = recipe.RequireDraft();

        var s10 = Phase("S10", "升温至固溶温度", StepType.Heat, 0, 80, 80, 180, "UP-固溶", "FURNACE",
            P(0, "目标温度", "℃", 530, 520, 540, true, true),
            P(1, "升温斜率", "℃/min", 8, 4, 12, true, false));
        var s20 = Phase("S20", "固溶保温", StepType.Hold, 1, 80, 280, 200, "UP-固溶", "FURNACE",
            P(0, "保温温度", "℃", 530, 525, 535, true, true),
            P(1, "保温时长", "s", 8, 1, 3600, true, true));
        var s30 = Phase("S30", "淬火冷却", StepType.Cool, 2, 520, 180, 120, "UP-淬火", "QUENCH",
            P(0, "终点温度", "℃", 40, 20, 60, true, true),
            P(1, "冷却时长", "s", 6, 1, 600, true, false));
        var s40 = Phase("S40", "汇合质检采样", StepType.QualityCheck, 3, 300, 480, 60, "UP-QC", null,
            P(0, "硬度下限", "HB", 95, 90, 110, false, true));

        draft.ReplaceProcedure(
            [s10, s20, s30, s40],
            [
                new RecipeEdge(draft.Id, s10.Id, s20.Id),
                new RecipeEdge(draft.Id, s20.Id, s40.Id),
                new RecipeEdge(draft.Id, s30.Id, s40.Id)
            ]);
        draft.Submit(DateTimeOffset.UtcNow.AddMinutes(-6), ApprovalChain.Standard, engineer.Id, engineer.DisplayName, "提交双单元并行示范配方");
        draft.Decide(supervisor.Id, supervisor.DisplayName, ApprovalDecision.Approved, "单元边界与汇合点正确", DateTimeOffset.UtcNow.AddMinutes(-4));
        draft.Decide(qa.Id, qa.DisplayName, ApprovalDecision.Approved, "并行握手不改变质检归档", DateTimeOffset.UtcNow.AddMinutes(-2));
        recipe.MarkApproved(draft);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(ct);

        RecipeStep Phase(string code, string name, StepType type, int ordinal, double x, double y, int watchdog, string unit, string? eqClass, params RecipeParameter[] parameters) =>
            new(draft.Id, code, name, type, ordinal, x, y, watchdog, null, parameters, null, unit, Isa88.DefaultOperation(type), null, eqClass);

        static RecipeParameter P(int slot, string name, string unit, double sp, double min, double max, bool write, bool qc) =>
            new(slot, name, unit, sp, min, max, write, qc);
    }

    /// <summary>
    /// 人工确认（禁止写 PLC）与质检超差保持示范配方。对已有库按编码幂等补齐。
    /// </summary>
    private static async Task EnsureConfirmAndOosDemoAsync(AppDbContext db, CancellationToken ct)
    {
        var engineer = await db.Users.SingleAsync(u => u.UserName == "engineer", ct);
        var supervisor = await db.Users.SingleAsync(u => u.UserName == "supervisor", ct);
        var qa = await db.Users.SingleAsync(u => u.UserName == "qa", ct);

        if (!await db.Recipes.AnyAsync(r => r.Code == "AL-HT-CFM", ct))
        {
            var recipe = MasterRecipe.Create("AL-HT-CFM", "人工确认闭环示范", "AL6061", "铝合金 6061 锻件",
                "升温完成后进入 ManualConfirm，调度 Idle PLC，等待电子签名，禁止 Trigger_Write。", engineer.Id);
            var draft = recipe.RequireDraft();
            var s10 = new RecipeStep(draft.Id, "S10", "升温至确认点", StepType.Heat, 0, 80, 120, 60, null,
                [P(0, "目标温度", "℃", 120, 100, 200, true, true),
                 P(1, "时长", "s", 1, 0.5, 5, true, true)],
                null, Isa88.DefaultUnitProcedure, Isa88.DefaultOperation(StepType.Heat), null, "FURNACE");
            var s20 = new RecipeStep(draft.Id, "S20", "操作员确认", StepType.ManualConfirm, 1, 360, 120, 120, null,
                [P(0, "确认意见", "", 0, null, null, false, false)],
                null, Isa88.DefaultUnitProcedure, Isa88.DefaultOperation(StepType.ManualConfirm), null, "FURNACE");
            draft.ReplaceProcedure([s10, s20], [new RecipeEdge(draft.Id, s10.Id, s20.Id)]);
            draft.Submit(DateTimeOffset.UtcNow.AddMinutes(-4), ApprovalChain.Standard, engineer.Id, engineer.DisplayName, "提交人工确认示范");
            draft.Decide(supervisor.Id, supervisor.DisplayName, ApprovalDecision.Approved, "确认工步不写 PLC", DateTimeOffset.UtcNow.AddMinutes(-3));
            draft.Decide(qa.Id, qa.DisplayName, ApprovalDecision.Approved, "电子签名放行", DateTimeOffset.UtcNow.AddMinutes(-2));
            recipe.MarkApproved(draft);
            db.Recipes.Add(recipe);
            await db.SaveChangesAsync(ct);
        }

        if (!await db.Recipes.AnyAsync(r => r.Code == "AL-HT-OOS", ct))
        {
            var recipe = MasterRecipe.Create("AL-HT-OOS", "质检超差保持示范", "AL6061", "铝合金 6061 锻件",
                "归档温度故意超出规格，握手 D 后 QualityOos 保持，禁止写下一步。", engineer.Id);
            var draft = recipe.RequireDraft();
            var s10 = new RecipeStep(draft.Id, "S10", "升温（规格故意过窄）", StepType.Heat, 0, 80, 120, 60, null,
                [P(0, "目标温度", "℃", 120, 200, 300, true, true),
                 P(1, "时长", "s", 1, 0.5, 5, true, true)],
                null, Isa88.DefaultUnitProcedure, Isa88.DefaultOperation(StepType.Heat), null, "FURNACE");
            var s20 = new RecipeStep(draft.Id, "S20", "保温（超差后不应写入）", StepType.Hold, 1, 360, 120, 60, null,
                [P(0, "保温温度", "℃", 120, 100, 200, true, true),
                 P(1, "保温时长", "s", 1, 0.5, 5, true, true)],
                null, Isa88.DefaultUnitProcedure, Isa88.DefaultOperation(StepType.Hold), null, "FURNACE");
            draft.ReplaceProcedure([s10, s20], [new RecipeEdge(draft.Id, s10.Id, s20.Id)]);
            draft.Submit(DateTimeOffset.UtcNow.AddMinutes(-4), ApprovalChain.Standard, engineer.Id, engineer.DisplayName, "提交超差保持示范");
            draft.Decide(supervisor.Id, supervisor.DisplayName, ApprovalDecision.Approved, "超差必须保持", DateTimeOffset.UtcNow.AddMinutes(-3));
            draft.Decide(qa.Id, qa.DisplayName, ApprovalDecision.Approved, "QualityOos 后由质量恢复", DateTimeOffset.UtcNow.AddMinutes(-2));
            recipe.MarkApproved(draft);
            db.Recipes.Add(recipe);
            await db.SaveChangesAsync(ct);
        }

        static RecipeParameter P(int slot, string name, string unit, double sp, double? min, double? max, bool write, bool qc) =>
            new(slot, name, unit, sp, min, max, write, qc);
    }

    /// <summary>
    /// Wait（禁止写 PLC，保持后续跑剩余）与 Mix/Pressure/Transfer 四步握手示范。
    /// </summary>
    private static async Task EnsureWaitAndProcessOpsDemoAsync(AppDbContext db, CancellationToken ct)
    {
        var engineer = await db.Users.SingleAsync(u => u.UserName == "engineer", ct);
        var supervisor = await db.Users.SingleAsync(u => u.UserName == "supervisor", ct);
        var qa = await db.Users.SingleAsync(u => u.UserName == "qa", ct);

        if (!await db.Recipes.AnyAsync(r => r.Code == "AL-HT-WAIT", ct))
        {
            var recipe = MasterRecipe.Create("AL-HT-WAIT", "上位机等待剩余时长示范", "AL6061", "铝合金 6061 锻件",
                "Heat 后 Wait 不写 PLC。保持时归档剩余秒数，恢复后续跑剩余，禁止整段重跑。", engineer.Id);
            var draft = recipe.RequireDraft();
            var s10 = new RecipeStep(draft.Id, "S10", "升温至等待点", StepType.Heat, 0, 80, 120, 60, null,
                [P(0, "目标温度", "℃", 120, 100, 200, true, true),
                 P(1, "时长", "s", 1, 0.5, 5, true, true)],
                null, Isa88.DefaultUnitProcedure, Isa88.DefaultOperation(StepType.Heat), null, "FURNACE");
            var s20 = new RecipeStep(draft.Id, "S20", "上位机等待", StepType.Wait, 1, 360, 120, 120, null,
                [P(0, "等待时长", "s", 8, 1, 60, false, false)],
                null, Isa88.DefaultUnitProcedure, Isa88.DefaultOperation(StepType.Wait), null, "FURNACE");
            draft.ReplaceProcedure([s10, s20], [new RecipeEdge(draft.Id, s10.Id, s20.Id)]);
            draft.Submit(DateTimeOffset.UtcNow.AddMinutes(-4), ApprovalChain.Standard, engineer.Id, engineer.DisplayName, "提交等待剩余示范");
            draft.Decide(supervisor.Id, supervisor.DisplayName, ApprovalDecision.Approved, "Wait 不写 PLC", DateTimeOffset.UtcNow.AddMinutes(-3));
            draft.Decide(qa.Id, qa.DisplayName, ApprovalDecision.Approved, "保持后续跑剩余", DateTimeOffset.UtcNow.AddMinutes(-2));
            recipe.MarkApproved(draft);
            db.Recipes.Add(recipe);
            await db.SaveChangesAsync(ct);
        }

        if (!await db.Recipes.AnyAsync(r => r.Code == "AL-PR-OPS", ct))
        {
            var recipe = MasterRecipe.Create("AL-PR-OPS", "搅拌加压转移示范", "AL6061", "铝合金 6061 锻件",
                "Mix / Pressure / Transfer 走四步握手写 PLC，汇合 QualityCheck 禁止写 PLC。", engineer.Id);
            var draft = recipe.RequireDraft();
            var s10 = new RecipeStep(draft.Id, "S10", "搅拌混合", StepType.Mix, 0, 80, 80, 60, null,
                [P(0, "搅拌转速", "rpm", 60, 10, 200, true, false),
                 P(1, "搅拌时长", "s", 1, 0.5, 60, true, true)],
                null, "UP-混合", Isa88.DefaultOperation(StepType.Mix), null, "PROCESS");
            var s20 = new RecipeStep(draft.Id, "S20", "加压保压", StepType.Pressure, 1, 290, 80, 60, null,
                [P(0, "目标压力", "bar", 2.5, 1, 6, true, true),
                 P(1, "保压时长", "s", 1, 0.5, 60, true, false)],
                null, "UP-加压", Isa88.DefaultOperation(StepType.Pressure), null, "PROCESS");
            var s30 = new RecipeStep(draft.Id, "S30", "转移出料", StepType.Transfer, 2, 500, 80, 60, null,
                [P(0, "转移量", "kg", 50, 1, 500, true, true, true),
                 P(1, "转移时长", "s", 1, 0.5, 60, true, false)],
                null, "UP-转移", Isa88.DefaultOperation(StepType.Transfer), null, "PROCESS");
            var s40 = new RecipeStep(draft.Id, "S40", "质检采样", StepType.QualityCheck, 3, 710, 80, 60, null,
                [P(0, "硬度下限", "HB", 95, 90, 110, false, true)],
                null, "UP-QC", Isa88.DefaultOperation(StepType.QualityCheck), null, "PROCESS");
            draft.ReplaceProcedure(
                [s10, s20, s30, s40],
                [
                    new RecipeEdge(draft.Id, s10.Id, s20.Id),
                    new RecipeEdge(draft.Id, s20.Id, s30.Id),
                    new RecipeEdge(draft.Id, s30.Id, s40.Id)
                ]);
            draft.Submit(DateTimeOffset.UtcNow.AddMinutes(-5), ApprovalChain.Standard, engineer.Id, engineer.DisplayName, "提交搅拌加压转移示范");
            draft.Decide(supervisor.Id, supervisor.DisplayName, ApprovalDecision.Approved, "工艺路径与单元边界正确", DateTimeOffset.UtcNow.AddMinutes(-4));
            draft.Decide(qa.Id, qa.DisplayName, ApprovalDecision.Approved, "质检不写 PLC", DateTimeOffset.UtcNow.AddMinutes(-2));
            recipe.MarkApproved(draft);
            db.Recipes.Add(recipe);
            await db.SaveChangesAsync(ct);
        }

        if (!await db.Equipment.AnyAsync(e => e.Code == "PR-01", ct))
        {
            var tagMap = JsonSerializer.Serialize(new HandshakeTagMap());
            var process = new EquipmentLine(
                "PR-01", "搅拌加压单元", PlcProtocol.Simulator, "127.0.0.1", 102,
                "S7_1200", 0, 1, tagMap, "搅拌加压/冲洗/气缸示范用进程内仿真器。");
            process.AssignClass("PROCESS");
            db.Equipment.Add(process);
            await db.SaveChangesAsync(ct);
        }

        if (!await db.Recipes.AnyAsync(r => r.Code == "AL-PR-CUSTOM", ct))
        {
            var recipe = MasterRecipe.Create("AL-PR-CUSTOM", "自定义程序号示范", "AL6061", "铝合金 6061 锻件",
                "水冲洗程序 21、气缸保压程序 22 走四步握手；汇合质检禁止写 PLC。开批绑 PROCESS 或 GENERIC。", engineer.Id);
            var draft = recipe.RequireDraft();
            var rinse = new RecipeStep(draft.Id, "S10", "水冲洗", StepType.Transfer, 0, 80, 80, 30, null,
                [P(0, "冲洗流量", "L/min", 12, 1, 40, true, false),
                 P(1, "冲洗时长", "s", 3, 0.5, 120, true, false)],
                null, "UP-冲洗", "OP-Rinse 水冲洗", 21, "PROCESS");
            var cyl = new RecipeStep(draft.Id, "S20", "气缸保压", StepType.Pressure, 1, 290, 80, 30, null,
                [P(0, "气缸压力", "bar", 4, 1, 10, true, true),
                 P(1, "保压时长", "s", 3, 0.5, 60, true, false)],
                null, "UP-冲洗", "OP-Cyl 气缸保压", 22, "PROCESS");
            var qc = new RecipeStep(draft.Id, "S30", "质检采样", StepType.QualityCheck, 2, 500, 80, 60, null,
                [P(0, "硬度下限", "HB", 95, 90, 110, false, true)],
                null, "UP-QC", Isa88.DefaultOperation(StepType.QualityCheck), null, "PROCESS");
            draft.ReplaceProcedure(
                [rinse, cyl, qc],
                [
                    new RecipeEdge(draft.Id, rinse.Id, cyl.Id),
                    new RecipeEdge(draft.Id, cyl.Id, qc.Id)
                ]);
            draft.Submit(DateTimeOffset.UtcNow.AddMinutes(-5), ApprovalChain.Standard, engineer.Id, engineer.DisplayName, "提交自定义程序号示范");
            draft.Decide(supervisor.Id, supervisor.DisplayName, ApprovalDecision.Approved, "程序 21/22 与相模板一致", DateTimeOffset.UtcNow.AddMinutes(-4));
            draft.Decide(qa.Id, qa.DisplayName, ApprovalDecision.Approved, "质检不写 PLC", DateTimeOffset.UtcNow.AddMinutes(-2));
            recipe.MarkApproved(draft);
            db.Recipes.Add(recipe);
            await db.SaveChangesAsync(ct);
        }

        static RecipeParameter P(int slot, string name, string unit, double sp, double? min, double? max, bool write, bool qc, bool scale = false) =>
            new(slot, name, unit, sp, min, max, write, qc, scale);
    }
}
