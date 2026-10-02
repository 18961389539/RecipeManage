using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Handshake;

namespace RecipesManage.Simulation;

public sealed class SimulatedPlcHandshakeClient : IPlcHandshakeClient
{
    private readonly SimulatedPlcStation _station;
    public SimulatedPlcHandshakeClient(SimulatedPlcStation station) => _station = station;
    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<PlcInboundSignals> ReadSignalsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_station.ReadSignals());
    public Task WriteStepPayloadAsync(int stepId, int stepType, IReadOnlyList<float> parameters, CancellationToken cancellationToken)
    {
        _station.WritePayload(stepId, stepType, parameters);
        return Task.CompletedTask;
    }
    public Task<PlcStepPayload> ReadStepPayloadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_station.ReadPayload());
    public Task SetTriggerWriteAsync(bool value, CancellationToken cancellationToken)
    {
        _station.SetTrigger(value);
        return Task.CompletedTask;
    }
    public Task SetHostHoldAsync(bool value, CancellationToken cancellationToken)
    {
        _station.SetHostHold(value);
        return Task.CompletedTask;
    }
    public Task ResetCompleteAsync(CancellationToken cancellationToken)
    {
        _station.ResetComplete();
        return Task.CompletedTask;
    }
    public Task<IReadOnlyDictionary<string, double>> ReadMeasuredAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_station.ReadMeasured());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
