using System.Text.Json;
using IoTClient.Clients.Modbus;
using IoTClient.Clients.PLC;
using IoTClient.Common.Enums;
using RecipesManage.Application.Contracts;
using RecipesManage.Domain.Equipment;
using RecipesManage.Domain.Handshake;

namespace RecipesManage.Infrastructure.Plc;

public sealed class PlcDriverFactory : IPlcDriverFactory
{
    private readonly IReadOnlyList<IPlcDriverProvider> _providers;

    public PlcDriverFactory() : this([])
    {
    }

    /// <param name="providers">
    /// 额外协议的提供方（目前只有仿真项目会提供 <see cref="PlcProtocol.Simulator"/>）。
    /// 没有提供方认领的协议，且不是下面三种真实协议，就明确报"未支持"，而不是悄悄退回仿真。
    /// </param>
    public PlcDriverFactory(IEnumerable<IPlcDriverProvider> providers) => _providers = providers.ToList();

    public IPlcHandshakeClient Create(EquipmentLine equipment)
    {
        foreach (var provider in _providers)
        {
            if (provider.Handles(equipment.Protocol))
                return provider.Create(equipment);
        }

        return equipment.Protocol switch
        {
            PlcProtocol.SiemensS7 => new SiemensHandshakeClient(equipment),
            PlcProtocol.ModbusTcp => new ModbusHandshakeClient(equipment),
            PlcProtocol.OpcUa => new OpcUaHandshakeClient(equipment),
            PlcProtocol.Simulator => throw new NotSupportedException(
                "设备协议是 Simulator，但本次部署没有启用仿真模块（RecipesManage.Simulation）。"),
            _ => throw new NotSupportedException($"未支持的协议 {equipment.Protocol}")
        };
    }
}
public abstract class MappedPlcHandshakeClient : IPlcHandshakeClient
{
    private static readonly JsonSerializerOptions TagJson = new() { PropertyNameCaseInsensitive = true };
    protected readonly HandshakeTagMap Map;
    protected static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(8);

    protected MappedPlcHandshakeClient(EquipmentLine equipment)
    {
        Map = string.IsNullOrWhiteSpace(equipment.TagMapJson)
            ? new HandshakeTagMap()
            : JsonSerializer.Deserialize<HandshakeTagMap>(equipment.TagMapJson, TagJson) ?? new HandshakeTagMap();
    }

    protected static Task RunIo(Action work, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            work();
        }, cancellationToken).WaitAsync(IoTimeout, cancellationToken);

    protected static Task<T> RunIo<T>(Func<T> work, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return work();
        }, cancellationToken).WaitAsync(IoTimeout, cancellationToken);

    public abstract Task ConnectAsync(CancellationToken cancellationToken);
    public abstract Task<PlcInboundSignals> ReadSignalsAsync(CancellationToken cancellationToken);
    public abstract Task WriteStepPayloadAsync(int stepId, int stepType, IReadOnlyList<float> parameters, CancellationToken cancellationToken);
    public abstract Task<PlcStepPayload> ReadStepPayloadAsync(CancellationToken cancellationToken);
    public abstract Task SetTriggerWriteAsync(bool value, CancellationToken cancellationToken);
    public abstract Task SetHostHoldAsync(bool value, CancellationToken cancellationToken);
    public abstract Task ResetCompleteAsync(CancellationToken cancellationToken);
    public abstract Task<IReadOnlyDictionary<string, double>> ReadMeasuredAsync(CancellationToken cancellationToken);
    public abstract ValueTask DisposeAsync();
}

public sealed class SiemensHandshakeClient : MappedPlcHandshakeClient
{
    private readonly SiemensClient _client;

    public SiemensHandshakeClient(EquipmentLine equipment) : base(equipment)
    {
        var version = ParseVersion(equipment.PlcModel);
        _client = new SiemensClient(version, equipment.Host, equipment.Port, (byte)equipment.Slot, (byte)equipment.Rack);
    }

    public override Task ConnectAsync(CancellationToken cancellationToken) => RunIo(() =>
    {
        var result = _client.Open();
        if (!result.IsSucceed)
            throw new InvalidOperationException($"S7 连接失败: {result.Err}");
    }, cancellationToken);

    public override Task<PlcInboundSignals> ReadSignalsAsync(CancellationToken cancellationToken) => RunIo(() =>
        new PlcInboundSignals(
            ReadBool(Map.PlcReady),
            ReadBool(Map.StepRunning),
            ReadBool(Map.StepComplete),
            ReadBool(Map.StepError),
            ReadInt(Map.ErrorCode),
            (uint)Math.Max(0, ReadInt(Map.Heartbeat)),
            ReadBool(Map.TriggerWrite),
            ReadOptionalBool(Map.PlcHeld),
            ReadOptionalBool(Map.HostHold)), cancellationToken);

    public override Task WriteStepPayloadAsync(int stepId, int stepType, IReadOnlyList<float> parameters, CancellationToken cancellationToken) => RunIo(() =>
    {
        Ensure(_client.Write(Map.StepId, stepId));
        Ensure(_client.Write(Map.StepType, stepType));
        for (var i = 0; i < Map.Params.Count && i < 16; i++)
        {
            var value = i < parameters.Count ? parameters[i] : 0f;
            Ensure(_client.Write(Map.Params[i], value));
        }
    }, cancellationToken);

    public override Task<PlcStepPayload> ReadStepPayloadAsync(CancellationToken cancellationToken) => RunIo(() =>
    {
        var count = Math.Min(Map.Params.Count, 16);
        var values = new float[count];
        for (var i = 0; i < count; i++)
            values[i] = ReadFloat(Map.Params[i]);
        return new PlcStepPayload(ReadInt(Map.StepId), ReadInt(Map.StepType), values);
    }, cancellationToken);

    public override Task SetTriggerWriteAsync(bool value, CancellationToken cancellationToken) => RunIo(() =>
    {
        Ensure(_client.Write(Map.TriggerWrite, value));
    }, cancellationToken);

    public override Task SetHostHoldAsync(bool value, CancellationToken cancellationToken) => RunIo(() =>
    {
        if (!string.IsNullOrWhiteSpace(Map.HostHold))
            Ensure(_client.Write(Map.HostHold, value));
    }, cancellationToken);

    public override Task ResetCompleteAsync(CancellationToken cancellationToken) => RunIo(() =>
    {
        Ensure(_client.Write(Map.StepComplete, false));
        Ensure(_client.Write(Map.StepError, false));
        Ensure(_client.Write(Map.ErrorCode, 0));
    }, cancellationToken);

    public override Task<IReadOnlyDictionary<string, double>> ReadMeasuredAsync(CancellationToken cancellationToken) => RunIo(() =>
    {
        var values = new Dictionary<string, double>();
        foreach (var (name, address) in Map.Measured)
            values[name] = ReadFloat(address);
        return (IReadOnlyDictionary<string, double>)values;
    }, cancellationToken);

    public override ValueTask DisposeAsync()
    {
        _client.Close();
        return ValueTask.CompletedTask;
    }

    private bool ReadBool(string address)
    {
        var result = _client.ReadBoolean(address);
        Ensure(result);
        return result.Value;
    }

    private bool ReadOptionalBool(string? address) =>
        string.IsNullOrWhiteSpace(address) ? false : ReadBool(address);

    private int ReadInt(string address)
    {
        var result = _client.ReadInt32(address);
        Ensure(result);
        return result.Value;
    }

    private float ReadFloat(string address)
    {
        var result = _client.ReadFloat(address);
        Ensure(result);
        return result.Value;
    }

    private static void Ensure(IoTClient.Result result)
    {
        if (!result.IsSucceed)
            throw new InvalidOperationException(result.Err);
    }

    private static void Ensure<T>(IoTClient.Result<T> result)
    {
        if (!result.IsSucceed)
            throw new InvalidOperationException(result.Err);
    }

    private static SiemensVersion ParseVersion(string model) => model.ToUpperInvariant() switch
    {
        "S7_200" => SiemensVersion.S7_200,
        "S7_200SMART" => SiemensVersion.S7_200Smart,
        "S7_300" => SiemensVersion.S7_300,
        "S7_400" => SiemensVersion.S7_400,
        "S7_1500" => SiemensVersion.S7_1500,
        _ => SiemensVersion.S7_1200
    };
}

public sealed class ModbusHandshakeClient : MappedPlcHandshakeClient
{
    private readonly ModbusTcpClient _client;

    public ModbusHandshakeClient(EquipmentLine equipment) : base(equipment)
    {
        _client = new ModbusTcpClient(equipment.Host, equipment.Port);
    }

    public override Task ConnectAsync(CancellationToken cancellationToken) => RunIo(() =>
    {
        var result = _client.Open();
        if (!result.IsSucceed)
            throw new InvalidOperationException($"Modbus 连接失败: {result.Err}");
    }, cancellationToken);

    public override Task<PlcInboundSignals> ReadSignalsAsync(CancellationToken cancellationToken) => RunIo(() =>
        new PlcInboundSignals(
            ReadBool(Map.PlcReady),
            ReadBool(Map.StepRunning),
            ReadBool(Map.StepComplete),
            ReadBool(Map.StepError),
            ReadInt(Map.ErrorCode),
            (uint)Math.Max(0, ReadInt(Map.Heartbeat)),
            ReadBool(Map.TriggerWrite),
            ReadOptionalBool(Map.PlcHeld),
            ReadOptionalBool(Map.HostHold)), cancellationToken);

    public override Task WriteStepPayloadAsync(int stepId, int stepType, IReadOnlyList<float> parameters, CancellationToken cancellationToken) => RunIo(() =>
    {
        Ensure(_client.Write(Map.StepId, stepId));
        Ensure(_client.Write(Map.StepType, stepType));
        for (var i = 0; i < Map.Params.Count && i < 16; i++)
        {
            var value = i < parameters.Count ? parameters[i] : 0f;
            Ensure(_client.Write(Map.Params[i], value));
        }
    }, cancellationToken);

    public override Task<PlcStepPayload> ReadStepPayloadAsync(CancellationToken cancellationToken) => RunIo(() =>
    {
        var count = Math.Min(Map.Params.Count, 16);
        var values = new float[count];
        for (var i = 0; i < count; i++)
            values[i] = ReadFloat(Map.Params[i]);
        return new PlcStepPayload(ReadInt(Map.StepId), ReadInt(Map.StepType), values);
    }, cancellationToken);

    public override Task SetTriggerWriteAsync(bool value, CancellationToken cancellationToken) => RunIo(() =>
    {
        Ensure(_client.Write(Map.TriggerWrite, value));
    }, cancellationToken);

    public override Task SetHostHoldAsync(bool value, CancellationToken cancellationToken) => RunIo(() =>
    {
        if (!string.IsNullOrWhiteSpace(Map.HostHold))
            Ensure(_client.Write(Map.HostHold, value));
    }, cancellationToken);

    public override Task ResetCompleteAsync(CancellationToken cancellationToken) => RunIo(() =>
    {
        Ensure(_client.Write(Map.StepComplete, false));
        Ensure(_client.Write(Map.StepError, false));
        Ensure(_client.Write(Map.ErrorCode, 0));
    }, cancellationToken);

    public override Task<IReadOnlyDictionary<string, double>> ReadMeasuredAsync(CancellationToken cancellationToken) => RunIo(() =>
    {
        var values = new Dictionary<string, double>();
        foreach (var (name, address) in Map.Measured)
            values[name] = ReadFloat(address);
        return (IReadOnlyDictionary<string, double>)values;
    }, cancellationToken);

    public override ValueTask DisposeAsync()
    {
        _client.Close();
        return ValueTask.CompletedTask;
    }

    private bool ReadBool(string address)
    {
        var result = _client.ReadCoil(address);
        Ensure(result);
        return result.Value;
    }

    private bool ReadOptionalBool(string? address) =>
        string.IsNullOrWhiteSpace(address) ? false : ReadBool(address);

    private int ReadInt(string address)
    {
        var result = _client.ReadInt32(address);
        Ensure(result);
        return result.Value;
    }

    private float ReadFloat(string address)
    {
        var result = _client.ReadFloat(address);
        Ensure(result);
        return result.Value;
    }

    private static void Ensure(IoTClient.Result result)
    {
        if (!result.IsSucceed)
            throw new InvalidOperationException(result.Err);
    }

    private static void Ensure<T>(IoTClient.Result<T> result)
    {
        if (!result.IsSucceed)
            throw new InvalidOperationException(result.Err);
    }
}
