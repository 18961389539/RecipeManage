using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipesManage.Domain.Equipment;

namespace RecipesManage.Simulation;

public sealed class SiemensS7LoopbackHostedService : IHostedService
{
    public const int DefaultPort = 1102;
    public const string EquipmentCode = "S7-01";

    private readonly SiemensS7HandshakeSlave _slave;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SiemensS7LoopbackHostedService> _log;
    private bool _bound;

    public SiemensS7LoopbackHostedService(
        SiemensS7HandshakeSlave slave,
        IServiceScopeFactory scopes,
        ILogger<SiemensS7LoopbackHostedService> log)
    {
        _slave = slave;
        _scopes = scopes;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var equipmentId = await PlcLoopbackGate.LoopbackEquipmentIdAsync(
            _scopes, EquipmentCode, PlcProtocol.SiemensS7, DefaultPort, _log, cancellationToken);
        if (equipmentId is null)
        {
            _log.LogInformation(
                "S7 环回从站未启动：没有把 {Code} 指向 127.0.0.1:{Port} 的启用设备行。",
                EquipmentCode, DefaultPort);
            return;
        }

        _bound = true;
        try
        {
            await _slave.StartAsync(DefaultPort, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "S7 环回从站端口 {Port} 无法绑定。", DefaultPort);
            return;
        }

        _slave.BindEquipment(equipmentId.Value);
        _log.LogInformation("IOTClient Siemens S7 环回从站监听 127.0.0.1:{Port}（ISO-on-TCP 四步握手，禁止盲写）。", _slave.Port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_bound) await _slave.DisposeAsync();
    }
}
