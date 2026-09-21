using System.Text.Json;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Handshake;
using RecipesManage.Infrastructure.Plc;
using Xunit;

namespace RecipesManage.Execution.Tests;

public sealed class OpcUaLoopbackTests
{
    [Fact]
    public async Task Client_ReadsPlcReady_WithoutWriting()
    {
        await using var slave = new OpcUaHandshakeSlave();
        await slave.StartAsync(0);
        var map = JsonSerializer.Serialize(HandshakeTagMap.OpcUaLoopback());
        var equipment = new EquipmentLine(
            "UA-T", "loop", PlcProtocol.OpcUa, slave.Endpoint, slave.Port,
            "OPC_UA", 0, 1, map, "it");
        await using var client = new OpcUaHandshakeClient(equipment);
        await client.ConnectAsync(CancellationToken.None);
        var signals = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.True(signals.PlcReady);
        Assert.False(signals.StepRunning);
        Assert.False(signals.TriggerWriteEcho);
    }

    [Fact]
    public async Task Client_FourStepHandshake_ArchivesMeasured()
    {
        await using var slave = new OpcUaHandshakeSlave();
        await slave.StartAsync(0);
        var map = JsonSerializer.Serialize(HandshakeTagMap.OpcUaLoopback());
        var equipment = new EquipmentLine(
            "UA-H", "loop", PlcProtocol.OpcUa, slave.Endpoint, slave.Port,
            "OPC_UA", 0, 1, map, "it");
        await using var client = new OpcUaHandshakeClient(equipment);
        await client.ConnectAsync(CancellationToken.None);

        await client.WriteStepPayloadAsync(10, 1, [120f, 1f], CancellationToken.None);
        await client.SetTriggerWriteAsync(true, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(12);
        PlcInboundSignals signals;
        do
        {
            await Task.Delay(150);
            signals = await client.ReadSignalsAsync(CancellationToken.None);
        } while (!signals.StepComplete && DateTime.UtcNow < deadline);

        Assert.True(signals.StepComplete);
        var measured = await client.ReadMeasuredAsync(CancellationToken.None);
        Assert.True(measured.ContainsKey("Temperature"));
        await client.ResetCompleteAsync(CancellationToken.None);
        signals = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.True(signals.PlcReady);
        Assert.False(signals.StepComplete);
    }
}
