using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipesManage.Infrastructure.Persistence;

namespace RecipesManage.Infrastructure.Plc;

public sealed class ModbusLoopbackHostedService : IHostedService
{
    public const int DefaultPort = 1502;
    public const string EquipmentCode = "MB-01";

    private readonly ModbusTcpHandshakeSlave _slave;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ModbusLoopbackHostedService> _log;
    private readonly int _port;

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
        try
        {
            await _slave.StartAsync(_port, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Modbus 环回从站端口 {Port} 无法绑定，IOTClient 联调请改端口或释放占用。", _port);
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
            _log.LogWarning(ex, "绑定 {Code} 到 Modbus 环回从站失败。", EquipmentCode);
        }

        _log.LogInformation("IOTClient Modbus TCP 环回从站监听 127.0.0.1:{Port}（四步握手，禁止盲写）。", _slave.Port);
    }

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await _slave.DisposeAsync();
}
