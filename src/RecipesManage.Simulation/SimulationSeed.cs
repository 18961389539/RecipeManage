using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Equipment;

namespace RecipesManage.Simulation;

/// <summary>
/// 三台环回从站对应的演示设备行（MB-01 / UA-01 / S7-01）。
///
/// 以前写在 <c>DatabaseSeeder</c> 里，于是生产程序集的种子代码要引用仿真类的端口常量。
/// 现在由宿主在 <c>Seed:Demo=true</c> 时显式调用；<c>Seed:Demo=false</c> 不调用，
/// 这几条设备行就不存在，<see cref="PlcLoopbackGate"/> 也就不会绑任何端口。
/// 幂等：按设备编码补齐，已存在的不覆盖（用户可能改过地址）。
/// </summary>
public static class SimulationSeed
{
    public static async Task EnsureLoopbackDevicesAsync(IAppDbContext db, CancellationToken ct = default)
    {
        await EnsureModbusAsync(db, ct);
        await EnsureOpcUaAsync(db, ct);
        await EnsureSiemensAsync(db, ct);
    }

    private static async Task EnsureModbusAsync(IAppDbContext db, CancellationToken ct)
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

    private static async Task EnsureOpcUaAsync(IAppDbContext db, CancellationToken ct)
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

    private static async Task EnsureSiemensAsync(IAppDbContext db, CancellationToken ct)
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
}
