using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Handshake;

namespace RecipesManage.Simulation;

/// <summary>认领 <see cref="PlcProtocol.Simulator"/>：进程内仿真，不走网络。</summary>
public sealed class SimulatedDriverProvider(SimulatedPlcRack rack) : IPlcDriverProvider
{
    public bool Handles(PlcProtocol protocol) => protocol == PlcProtocol.Simulator;

    public IPlcHandshakeClient Create(EquipmentLine equipment) =>
        new SimulatedPlcHandshakeClient(rack.Get(equipment.Id));
}

/// <summary>
/// 故障注入：进程内仿真站一定注入；绑在该设备上的环回从站（Modbus / OPC UA / S7）也一并注入，
/// 这样同一个"注入故障"按钮对所有仿真形态行为一致。
/// </summary>
public sealed class SimulatorControl(
    SimulatedPlcRack rack,
    ModbusTcpHandshakeSlave? modbus = null,
    OpcUaHandshakeSlave? opcUa = null,
    SiemensS7HandshakeSlave? s7 = null) : ISimulatorControl
{
    public void InjectFault(Guid equipmentId, string mode)
    {
        rack.Get(equipmentId).InjectFault(mode);
        if (modbus is not null && modbus.IsBoundTo(equipmentId))
            modbus.Station.InjectFault(mode);
        if (opcUa is not null && opcUa.IsBoundTo(equipmentId))
            opcUa.Station.InjectFault(mode);
        if (s7 is not null && s7.IsBoundTo(equipmentId))
            s7.Station.InjectFault(mode);
    }
}

public static class SimulationServiceCollectionExtensions
{
    /// <summary>
    /// 进程内仿真站（<see cref="PlcProtocol.Simulator"/>）+ 故障注入。
    /// 不含环回从站：从站要占 TCP 端口，需要时再调 <see cref="AddPlcLoopbackSimulators"/>。
    /// </summary>
    public static IServiceCollection AddPlcSimulation(this IServiceCollection services)
    {
        services.TryAddSingleton<SimulatedPlcRack>();
        services.AddSingleton<IPlcDriverProvider, SimulatedDriverProvider>();
        services.TryAddSingleton<ISimulatorControl>(sp => new SimulatorControl(
            sp.GetRequiredService<SimulatedPlcRack>(),
            sp.GetService<ModbusTcpHandshakeSlave>(),
            sp.GetService<OpcUaHandshakeSlave>(),
            sp.GetService<SiemensS7HandshakeSlave>()));
        return services;
    }

    /// <summary>
    /// 三个协议栈环回从站。是否真的绑端口仍由 <see cref="PlcLoopbackGate"/> 按库里的设备行在启动时判定，
    /// 所以注册它们不等于占端口。
    /// </summary>
    public static IServiceCollection AddPlcLoopbackSimulators(this IServiceCollection services)
    {
        services.AddSingleton<ModbusTcpHandshakeSlave>();
        services.AddHostedService<ModbusLoopbackHostedService>();
        services.AddSingleton<OpcUaHandshakeSlave>();
        services.AddHostedService<OpcUaLoopbackHostedService>();
        services.AddSingleton<SiemensS7HandshakeSlave>();
        services.AddHostedService<SiemensS7LoopbackHostedService>();
        return services;
    }
}
