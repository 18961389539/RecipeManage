using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecipesManage.Domain.Equipment;
using RecipesManage.Application.Contracts;

namespace RecipesManage.Simulation;

/// <summary>
/// 环回从站要不要真的起来。
///
/// 三个从站各自占一个 TCP 端口（OPC UA 还要建 PKI 目录、起 40ms 发布定时器），
/// 而单设备现场最常见的配置是 <see cref="PlcProtocol.Simulator" />（进程内仿真，根本不走 TCP）
/// 或者一台真实 PLC 的 IP。以前它们无条件启动，等于在没有对端的情况下白占端口、白养线程。
///
/// 判据用库里那条设备行，而不是再加一个配置文件开关：设备行是这台机器"要连什么"的唯一真源，
/// 而且要求它指向回环地址——协议对得上但地址是 10.0.0.5 的设备，连的是真 PLC，
/// 再把本机从站拉起来只会让人误以为"环回在跑"。
/// </summary>
public static class PlcLoopbackGate
{
    /// <summary>返回该绑到从站上的设备 Id；null 表示这个从站这次不该启动（或那条设备行不需要它）。</summary>
    public static async Task<Guid?> LoopbackEquipmentIdAsync(
        IServiceScopeFactory scopes,
        string equipmentCode,
        PlcProtocol protocol,
        int port,
        ILogger log,
        CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var line = await db.Equipment.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Code == equipmentCode, ct);
            return line is not null && TargetsLoopback(line, protocol, port) ? line.Id : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读不到设备行就不占端口。以前是"从站先起来、绑定失败只是没对端"，
            // 现在少个仿真器远轻于让整个宿主 StartAsync 抛穿。
            log.LogWarning(ex, "读取 {Code} 设备行失败，{Protocol} 环回从站本次不启动。", equipmentCode, protocol);
            return null;
        }
    }

    public static bool TargetsLoopback(EquipmentLine line, PlcProtocol protocol, int port) =>
        line.Enabled
        && line.Protocol == protocol
        && line.Port == port
        && IsLoopbackHost(line.Host);

    /// <summary>OPC UA 的 Host 存的是完整 <c>opc.tcp://…</c> 端点，所以按"含回环地址"判，而不是相等。</summary>
    private static bool IsLoopbackHost(string host) =>
        host.Contains("127.0.0.1", StringComparison.Ordinal)
        || host.Contains("::1", StringComparison.Ordinal)
        || host.Contains("localhost", StringComparison.OrdinalIgnoreCase);
}
