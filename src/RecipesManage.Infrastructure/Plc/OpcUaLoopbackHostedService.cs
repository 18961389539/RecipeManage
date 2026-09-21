using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipesManage.Infrastructure.Persistence;

namespace RecipesManage.Infrastructure.Plc;

public sealed class OpcUaLoopbackHostedService : IHostedService
{
    public const int DefaultPort = 48410;
    public const string EquipmentCode = "UA-01";

    private readonly OpcUaHandshakeSlave _slave;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OpcUaLoopbackHostedService> _log;

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
        try
        {
            await _slave.StartAsync(DefaultPort, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OPC UA 环回从站端口 {Port} 无法启动，请改端口或释放占用。", DefaultPort);
            return;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var equipment = await db.Equipment.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Code == EquipmentCode, cancellationToken);
            if (equipment is not null)
                _slave.BindEquipment(equipment.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "绑定 {Code} 到 OPC UA 环回从站失败。", EquipmentCode);
        }

        _log.LogInformation("OPC UA 环回从站监听 {Endpoint}（四步握手，禁止盲写）。", _slave.Endpoint);
    }

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await _slave.DisposeAsync();
}
