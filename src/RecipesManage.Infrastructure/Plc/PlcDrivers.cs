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
    private readonly SimulatedPlcRack _rack;
    private readonly ModbusTcpHandshakeSlave? _modbusLoopback;
    private readonly OpcUaHandshakeSlave? _opcUaLoopback;
    private readonly SiemensS7HandshakeSlave? _s7Loopback;

    public PlcDriverFactory(SimulatedPlcRack rack) : this(rack, null, null, null)
    {
    }

    public PlcDriverFactory(SimulatedPlcRack rack, ModbusTcpHandshakeSlave? modbusLoopback)
        : this(rack, modbusLoopback, null, null)
    {
    }

    public PlcDriverFactory(
        SimulatedPlcRack rack,
        ModbusTcpHandshakeSlave? modbusLoopback,
        OpcUaHandshakeSlave? opcUaLoopback)
        : this(rack, modbusLoopback, opcUaLoopback, null)
    {
    }

    public PlcDriverFactory(
        SimulatedPlcRack rack,
        ModbusTcpHandshakeSlave? modbusLoopback,
        OpcUaHandshakeSlave? opcUaLoopback,
        SiemensS7HandshakeSlave? s7Loopback)
    {
        _rack = rack;
        _modbusLoopback = modbusLoopback;
        _opcUaLoopback = opcUaLoopback;
        _s7Loopback = s7Loopback;
    }

    public IPlcHandshakeClient Create(EquipmentLine equipment) =>
        equipment.Protocol switch
        {
            PlcProtocol.Simulator => new SimulatedPlcHandshakeClient(_rack.Get(equipment.Id)),
            PlcProtocol.SiemensS7 => new SiemensHandshakeClient(equipment),
            PlcProtocol.ModbusTcp => new ModbusHandshakeClient(equipment),
            PlcProtocol.OpcUa => new OpcUaHandshakeClient(equipment),
            _ => throw new NotSupportedException($"未支持的协议 {equipment.Protocol}")
        };

    public void InjectSimulatorFault(Guid equipmentId, string mode)
    {
        _rack.Get(equipmentId).InjectFault(mode);
        if (_modbusLoopback is not null && _modbusLoopback.IsBoundTo(equipmentId))
            _modbusLoopback.Station.InjectFault(mode);
        if (_opcUaLoopback is not null && _opcUaLoopback.IsBoundTo(equipmentId))
            _opcUaLoopback.Station.InjectFault(mode);
        if (_s7Loopback is not null && _s7Loopback.IsBoundTo(equipmentId))
            _s7Loopback.Station.InjectFault(mode);
    }
}

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
