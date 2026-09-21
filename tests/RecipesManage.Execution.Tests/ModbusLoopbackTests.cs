using System.Text.Json;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Handshake;
using RecipesManage.Infrastructure.Plc;
using Xunit;

namespace RecipesManage.Execution.Tests;

public sealed class ModbusLoopbackTests
{
    [Fact]
    public async Task IoTClient_ReadsPlcReady_WithoutWriting()
    {
        await using var slave = new ModbusTcpHandshakeSlave();
        await slave.StartAsync(0);
        var map = JsonSerializer.Serialize(HandshakeTagMap.ModbusLoopback());
        var equipment = new EquipmentLine(
            "MB-T", "loop", PlcProtocol.ModbusTcp, "127.0.0.1", slave.Port,
            "MODBUS", 0, 1, map, "it");
        await using var client = new ModbusHandshakeClient(equipment);
        await client.ConnectAsync(CancellationToken.None);
        var signals = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.True(signals.PlcReady);
        Assert.False(signals.StepRunning);
        Assert.False(signals.TriggerWriteEcho);
    }

    [Fact]
    public async Task IoTClient_FourStepHandshake_ArchivesMeasured()
    {
        await using var slave = new ModbusTcpHandshakeSlave();
        await slave.StartAsync(0);
        var map = JsonSerializer.Serialize(HandshakeTagMap.ModbusLoopback());
        var equipment = new EquipmentLine(
            "MB-H", "loop", PlcProtocol.ModbusTcp, "127.0.0.1", slave.Port,
            "MODBUS", 0, 1, map, "it");
        await using var client = new ModbusHandshakeClient(equipment);
        await client.ConnectAsync(CancellationToken.None);

        await client.WriteStepPayloadAsync(10, 1, [120f, 1f], CancellationToken.None);
        await client.SetTriggerWriteAsync(true, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(8);
        PlcInboundSignals signals;
        do
        {
            await Task.Delay(100);
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
