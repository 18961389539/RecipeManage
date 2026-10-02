using RecipesManage.Domain.Equipment;
using RecipesManage.Infrastructure.Plc;
using Xunit;
using RecipesManage.Simulation;

namespace RecipesManage.Execution.Tests;

/// <summary>
/// 环回从站的启动判据。这些端口一旦占上就没人看得见，所以"该不该起"必须能被读出来：
/// 现场最常见的两种配置（进程内 Simulator、指向真实 PLC 的 IP）都不该有本机从站在跑。
/// </summary>
public static class PlcLoopbackGateTests
{
    private static EquipmentLine Line(
        PlcProtocol protocol, string host, int port, bool enabled = true)
    {
        var line = new EquipmentLine(
            "MB-01", "环回从站", protocol, host, port, "MODBUS", 0, 1, "{}", "测试用");
        if (!enabled)
            line.Update(line.Name, protocol, host, port, "MODBUS", 0, 1, false, "{}", "测试用");
        return line;
    }

    [Fact]
    public static void StartsOnlyForAnEnabledLinePointedAtTheLoopbackPort()
    {
        Assert.True(PlcLoopbackGate.TargetsLoopback(
            Line(PlcProtocol.ModbusTcp, "127.0.0.1", 1502), PlcProtocol.ModbusTcp, 1502));

        // 进程内仿真根本不走 TCP：给它起个 1502 的从站纯属白占端口。
        Assert.False(PlcLoopbackGate.TargetsLoopback(
            Line(PlcProtocol.Simulator, "127.0.0.1", 1502), PlcProtocol.ModbusTcp, 1502));

        // 连的是真 PLC：本机从站起来只会让人误以为"环回在跑"。
        Assert.False(PlcLoopbackGate.TargetsLoopback(
            Line(PlcProtocol.ModbusTcp, "10.0.0.5", 1502), PlcProtocol.ModbusTcp, 1502));

        // 设备行改了端口，而监听者还按默认端口起 —— 两边对不上，别起。
        Assert.False(PlcLoopbackGate.TargetsLoopback(
            Line(PlcProtocol.ModbusTcp, "127.0.0.1", 1503), PlcProtocol.ModbusTcp, 1502));

        // 停用 = 明确不要。
        Assert.False(PlcLoopbackGate.TargetsLoopback(
            Line(PlcProtocol.ModbusTcp, "127.0.0.1", 1502, enabled: false), PlcProtocol.ModbusTcp, 1502));

        // 协议串了（把 MB-01 改成 S7）也不该由 Modbus 从站来顶。
        Assert.False(PlcLoopbackGate.TargetsLoopback(
            Line(PlcProtocol.SiemensS7, "127.0.0.1", 1502), PlcProtocol.ModbusTcp, 1502));
    }

    [Fact]
    public static void AcceptsEverySpellingOfTheLoopbackAddress()
    {
        // OPC UA 的 Host 存的是完整端点 URL，所以只能按"含回环地址"判。
        Assert.True(PlcLoopbackGate.TargetsLoopback(
            Line(PlcProtocol.OpcUa, "opc.tcp://127.0.0.1:48410/brmes", 48410), PlcProtocol.OpcUa, 48410));
        Assert.True(PlcLoopbackGate.TargetsLoopback(
            Line(PlcProtocol.OpcUa, "opc.tcp://localhost:48410/brmes", 48410), PlcProtocol.OpcUa, 48410));
        Assert.True(PlcLoopbackGate.TargetsLoopback(
            Line(PlcProtocol.OpcUa, "opc.tcp://[::1]:48410/brmes", 48410), PlcProtocol.OpcUa, 48410));

        // 网桥/容器里的 172.17.x.x 不是回环。
        Assert.False(PlcLoopbackGate.TargetsLoopback(
            Line(PlcProtocol.OpcUa, "opc.tcp://172.17.0.2:48410/brmes", 48410), PlcProtocol.OpcUa, 48410));
    }
}
