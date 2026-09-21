using RecipesManage.Domain.Handshake;
using RecipesManage.Infrastructure.Plc;
using Xunit;

namespace RecipesManage.Execution.Tests;

public class SimulatedPlcHandshakeTests
{
    [Fact]
    public async Task Simulator_accepts_write_only_when_ready_then_runs_and_completes()
    {
        var station = new SimulatedPlcStation();
        var client = new SimulatedPlcHandshakeClient(station);

        var idle = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.True(idle.PlcReady);
        Assert.False(idle.StepRunning);

        await client.WriteStepPayloadAsync(1, 2, [175f, 1f], CancellationToken.None);
        await client.SetTriggerWriteAsync(true, CancellationToken.None);

        var running = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.True(running.StepRunning);
        Assert.False(running.PlcReady);

        PlcInboundSignals done;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            await Task.Delay(50);
            done = await client.ReadSignalsAsync(CancellationToken.None);
        } while (!done.StepComplete && sw.Elapsed < TimeSpan.FromSeconds(5));

        Assert.True(done.StepComplete);
        Assert.False(done.StepRunning);

        var measured = await client.ReadMeasuredAsync(CancellationToken.None);
        Assert.True(measured["Temperature"] > 0);

        await client.ResetCompleteAsync(CancellationToken.None);
        var ready = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.True(ready.PlcReady);
        Assert.False(ready.StepComplete);
    }

    [Fact]
    public async Task Trigger_while_not_ready_sets_plc_error()
    {
        var station = new SimulatedPlcStation();
        var client = new SimulatedPlcHandshakeClient(station);
        await client.WriteStepPayloadAsync(1, 1, [100f, 1f], CancellationToken.None);
        await client.SetTriggerWriteAsync(true, CancellationToken.None);
        await client.SetTriggerWriteAsync(true, CancellationToken.None);
        var signals = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.True(signals.StepError);
        Assert.Equal(0x10, signals.ErrorCode);
    }

    [Fact]
    public async Task HoldNotReady_never_goes_running_so_host_cannot_blind_write()
    {
        var station = new SimulatedPlcStation();
        station.InjectFault("HoldNotReady");
        var client = new SimulatedPlcHandshakeClient(station);
        var idle = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.False(idle.PlcReady);
        Assert.False(idle.StepRunning);
        await client.WriteStepPayloadAsync(2, 1, [530f, 8f], CancellationToken.None);
        await client.SetTriggerWriteAsync(true, CancellationToken.None);
        var after = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.False(after.PlcReady);
        Assert.False(after.StepRunning);
        Assert.False(after.StepComplete);
    }

    [Fact]
    public async Task NoAck_keeps_step_running_false_after_trigger()
    {
        var station = new SimulatedPlcStation();
        station.InjectFault("NoAck");
        var client = new SimulatedPlcHandshakeClient(station);
        Assert.True((await client.ReadSignalsAsync(CancellationToken.None)).PlcReady);
        await client.WriteStepPayloadAsync(1, 1, [100f, 1f], CancellationToken.None);
        await client.SetTriggerWriteAsync(true, CancellationToken.None);
        var signals = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.False(signals.PlcReady);
        Assert.False(signals.StepRunning);
        Assert.False(signals.StepComplete);
    }

    [Fact]
    public async Task HostHold_PausesRunningStep_ResumeContinuesRemaining()
    {
        var station = new SimulatedPlcStation();
        var client = new SimulatedPlcHandshakeClient(station);
        await client.WriteStepPayloadAsync(10, 1, [120f, 6f], CancellationToken.None);
        await client.SetTriggerWriteAsync(true, CancellationToken.None);
        var running = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.True(running.StepRunning);

        await client.SetHostHoldAsync(true, CancellationToken.None);
        var held = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.True(held.PlcHeld);
        Assert.True(held.HostHoldEcho);
        Assert.False(held.StepRunning);
        Assert.False(held.StepComplete);

        await Task.Delay(800);
        var stillHeld = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.True(stillHeld.PlcHeld);
        Assert.False(stillHeld.StepComplete);

        await client.SetHostHoldAsync(false, CancellationToken.None);
        var resumed = await client.ReadSignalsAsync(CancellationToken.None);
        Assert.False(resumed.PlcHeld);
        Assert.True(resumed.StepRunning);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        PlcInboundSignals done;
        do
        {
            await Task.Delay(50);
            done = await client.ReadSignalsAsync(CancellationToken.None);
        } while (!done.StepComplete && sw.Elapsed < TimeSpan.FromSeconds(8));

        Assert.True(done.StepComplete);
        Assert.False(done.PlcHeld);
    }

    [Fact]
    public async Task WritePayload_Echoes_StepIdAndParameters()
    {
        var station = new SimulatedPlcStation();
        var client = new SimulatedPlcHandshakeClient(station);
        await client.WriteStepPayloadAsync(10, 1, [530f, 8f], CancellationToken.None);
        var echo = await client.ReadStepPayloadAsync(CancellationToken.None);
        Assert.True(PlcWriteVerify.Matches(10, 1, [530f, 8f], echo, out _));
        Assert.Equal(10, echo.StepId);
        Assert.Equal(530f, echo.Parameters[0]);
    }

    [Fact]
    public async Task CorruptEcho_Mismatches_WrittenStepId()
    {
        var station = new SimulatedPlcStation();
        station.InjectFault("CorruptEcho");
        var client = new SimulatedPlcHandshakeClient(station);
        await client.WriteStepPayloadAsync(10, 1, [530f, 8f], CancellationToken.None);
        var echo = await client.ReadStepPayloadAsync(CancellationToken.None);
        Assert.False(PlcWriteVerify.Matches(10, 1, [530f, 8f], echo, out _));
        Assert.Equal(11, echo.StepId);
    }

    [Fact]
    public async Task Param15_ProcessDuration_Overrides_Param1_Ramp()
    {
        var station = new SimulatedPlcStation();
        var client = new SimulatedPlcHandshakeClient(station);
        var payload = new float[16];
        payload[0] = 530f;
        payload[1] = 8f;
        payload[15] = 1f;
        await client.WriteStepPayloadAsync(10, 1, payload, CancellationToken.None);
        await client.SetTriggerWriteAsync(true, CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        PlcInboundSignals done;
        do
        {
            await Task.Delay(40);
            done = await client.ReadSignalsAsync(CancellationToken.None);
        } while (!done.StepComplete && sw.Elapsed < TimeSpan.FromSeconds(3.5));

        Assert.True(done.StepComplete);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "Param[15]=1s 应覆盖升温斜率 Param[1]=8s");
    }
}
