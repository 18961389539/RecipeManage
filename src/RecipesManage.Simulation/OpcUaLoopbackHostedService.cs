using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipesManage.Domain.Equipment;

namespace RecipesManage.Simulation;

public sealed class OpcUaLoopbackHostedService : IHostedService
{
    public const int DefaultPort = 48410;
    public const string EquipmentCode = "UA-01";

    private readonly OpcUaHandshakeSlave _slave;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OpcUaLoopbackHostedService> _log;
    private bool _bound;

    public OpcUaLoopbackHostedService(
        OpcUaHandshakeSlave slave,
        IServiceScopeFactory scopes,
        ILogger<OpcUaLoopbackHostedService> log)
    {
        _slave = slave;
        _scopes = scopes;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var equipmentId = await PlcLoopbackGate.LoopbackEquipmentIdAsync(
            _scopes, EquipmentCode, PlcProtocol.OpcUa, DefaultPort, _log, cancellationToken);
        if (equipmentId is null)
        {
            // 没起来就不建 PKI 目录、不起发布定时器：这三样在无人对的机器上只是噪音。
            _log.LogInformation(
                "OPC UA 环回从站未启动：没有把 {Code} 指向回环 {Port} 的启用设备行。",
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
            _log.LogWarning(ex, "OPC UA 环回从站端口 {Port} 无法启动，请改端口或释放占用。", DefaultPort);
            return;
        }

        _slave.BindEquipment(equipmentId.Value);
        _log.LogInformation("OPC UA 环回从站监听 {Endpoint}（四步握手，禁止盲写）。", _slave.Endpoint);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_bound) await _slave.DisposeAsync();
    }
}
