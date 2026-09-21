using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipesManage.Infrastructure.Persistence;

namespace RecipesManage.Infrastructure.Plc;

public sealed class SiemensS7LoopbackHostedService : IHostedService
{
    public const int DefaultPort = 1102;
    public const string EquipmentCode = "S7-01";

    private readonly SiemensS7HandshakeSlave _slave;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SiemensS7LoopbackHostedService> _log;

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
        try
        {
            await _slave.StartAsync(DefaultPort, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "S7 环回从站端口 {Port} 无法绑定。", DefaultPort);
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
            _log.LogWarning(ex, "绑定 {Code} 到 S7 环回从站失败。", EquipmentCode);
        }

        _log.LogInformation("IOTClient Siemens S7 环回从站监听 127.0.0.1:{Port}（ISO-on-TCP 四步握手，禁止盲写）。", _slave.Port);
    }

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await _slave.DisposeAsync();
}
