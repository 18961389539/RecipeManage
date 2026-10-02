using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipesManage.Domain.Equipment;

namespace RecipesManage.Simulation;

public sealed class ModbusLoopbackHostedService : IHostedService
{
    public const int DefaultPort = 1502;
    public const string EquipmentCode = "MB-01";

    private readonly ModbusTcpHandshakeSlave _slave;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ModbusLoopbackHostedService> _log;
    private readonly int _port;
    private bool _bound;

    public ModbusLoopbackHostedService(
        ModbusTcpHandshakeSlave slave,
        IServiceScopeFactory scopes,
        ILogger<ModbusLoopbackHostedService> log)
    {
        _slave = slave;
        _scopes = scopes;
        _log = log;
        _port = DefaultPort;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var equipmentId = await PlcLoopbackGate.LoopbackEquipmentIdAsync(
            _scopes, EquipmentCode, PlcProtocol.ModbusTcp, _port, _log, cancellationToken);
        if (equipmentId is null)
        {
            _log.LogInformation(
                "Modbus 环回从站未启动：没有把 {Code} 指向 127.0.0.1:{Port} 的启用设备行。",
                EquipmentCode, _port);
            return;
        }

        _bound = true;
        try
        {
            await _slave.StartAsync(_port, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Modbus 环回从站端口 {Port} 无法绑定，IOTClient 联调请改端口或释放占用。", _port);
            return;
        }

        _slave.BindEquipment(equipmentId.Value);
        _log.LogInformation("IOTClient Modbus TCP 环回从站监听 127.0.0.1:{Port}（四步握手，禁止盲写）。", _slave.Port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_bound) await _slave.DisposeAsync();
    }
}
