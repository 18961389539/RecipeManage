using System.Net;
using System.Net.Sockets;
using RecipesManage.Domain.Equipment;

namespace RecipesManage.Infrastructure.Plc;

/// <summary>
/// 本机 Modbus TCP 从站：把 IOTClient 主站读写映射到 <see cref="SimulatedPlcStation"/> 四步握手状态机。
/// 开发环境用于验证真实协议栈，不替代现场 PLC。
/// </summary>
public sealed class ModbusTcpHandshakeSlave : IAsyncDisposable
{
    public SimulatedPlcStation Station { get; } = new();
    public HandshakeTagMap Map { get; } = HandshakeTagMap.ModbusLoopback();
    public int Port { get; private set; }
    public Guid? BoundEquipmentId { get; private set; }

    private readonly object _gate = new();
    private readonly ushort[] _holding = new ushort[128];
    private TcpListener? _listener;
    private CancellationTokenSource? _runCts;
    private Task? _accept;

    public void BindEquipment(Guid equipmentId) => BoundEquipmentId = equipmentId;

    public bool IsBoundTo(Guid equipmentId) => BoundEquipmentId == equipmentId;

    public Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _accept = AcceptLoopAsync(_runCts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _listener is not null)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => ServeAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            var header = new byte[7];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await ReadExactAsync(stream, header, 7, ct).ConfigureAwait(false);
                    var length = (header[4] << 8) | header[5];
                    if (length < 1 || length > 260)
                        return;
                    var pdu = new byte[length - 1];
                    await ReadExactAsync(stream, pdu, pdu.Length, ct).ConfigureAwait(false);
                    var reply = HandlePdu(pdu);
                    var response = new byte[7 + reply.Length];
                    Buffer.BlockCopy(header, 0, response, 0, 4);
                    var respLen = 1 + reply.Length;
                    response[4] = (byte)(respLen >> 8);
                    response[5] = (byte)respLen;
                    response[6] = header[6];
                    Buffer.BlockCopy(reply, 0, response, 7, reply.Length);
                    await stream.WriteAsync(response, ct).ConfigureAwait(false);
                }
            }
            catch (EndOfStreamException)
            {
                // client closed
            }
            catch (IOException)
            {
                // reset
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }
    }

    private byte[] HandlePdu(byte[] pdu)
    {
        if (pdu.Length < 1)
            return ExceptionPdu(0, 0x01);
        var fc = pdu[0];
        try
        {
            PublishInbound();
            return fc switch
            {
                1 or 2 => ReadBits(pdu, coils: fc == 1),
                3 or 4 => ReadRegisters(pdu),
                5 => WriteCoil(pdu),
                6 => WriteSingleRegister(pdu),
                15 => WriteCoils(pdu),
                16 => WriteRegisters(pdu),
                _ => ExceptionPdu(fc, 0x01)
            };
        }
        catch
        {
            return ExceptionPdu(fc, 0x04);
        }
    }

    private void PublishInbound()
    {
        var signals = Station.ReadSignals();
        var measured = Station.ReadMeasured();
        lock (_gate)
        {
            SetCoil(Parse(Map.PlcReady), signals.PlcReady);
            SetCoil(Parse(Map.StepRunning), signals.StepRunning);
            SetCoil(Parse(Map.StepComplete), signals.StepComplete);
            SetCoil(Parse(Map.StepError), signals.StepError);
            if (!string.IsNullOrWhiteSpace(Map.PlcHeld))
                SetCoil(Parse(Map.PlcHeld), signals.PlcHeld);
            if (!string.IsNullOrWhiteSpace(Map.HostHold))
                SetCoil(Parse(Map.HostHold), signals.HostHoldEcho);
            WriteInt32(Parse(Map.ErrorCode), signals.ErrorCode);
            WriteInt32(Parse(Map.Heartbeat), (int)signals.Heartbeat);
            foreach (var (name, address) in Map.Measured)
            {
                if (measured.TryGetValue(name, out var value))
                    WriteFloat(Parse(address), (float)value);
            }
        }
    }

    private readonly bool[] _coils = new bool[32];

    private byte[] ReadBits(byte[] pdu, bool coils)
    {
        if (pdu.Length < 5)
            return ExceptionPdu(pdu[0], 0x03);
        var start = (pdu[1] << 8) | pdu[2];
        var qty = (pdu[3] << 8) | pdu[4];
        if (qty is < 1 or > 2000)
            return ExceptionPdu(pdu[0], 0x03);
        var byteCount = (qty + 7) / 8;
        var data = new byte[2 + byteCount];
        data[0] = pdu[0];
        data[1] = (byte)byteCount;
        lock (_gate)
        {
            for (var i = 0; i < qty; i++)
            {
                if (GetCoil(start + i))
                    data[2 + i / 8] |= (byte)(1 << (i % 8));
            }
        }

        _ = coils;
        return data;
    }

    private byte[] ReadRegisters(byte[] pdu)
    {
        if (pdu.Length < 5)
            return ExceptionPdu(pdu[0], 0x03);
        var start = (pdu[1] << 8) | pdu[2];
        var qty = (pdu[3] << 8) | pdu[4];
        if (qty is < 1 or > 125)
            return ExceptionPdu(pdu[0], 0x03);
        var data = new byte[2 + qty * 2];
        data[0] = pdu[0];
        data[1] = (byte)(qty * 2);
        lock (_gate)
        {
            for (var i = 0; i < qty; i++)
            {
                var value = GetHolding(start + i);
                data[2 + i * 2] = (byte)(value >> 8);
                data[3 + i * 2] = (byte)value;
            }
        }

        return data;
    }

    private byte[] WriteCoil(byte[] pdu)
    {
        if (pdu.Length < 5)
            return ExceptionPdu(5, 0x03);
        var address = (pdu[1] << 8) | pdu[2];
        var value = pdu[3] == 0xFF;
        ApplyCoilWrite(address, value);
        return pdu.AsSpan(0, 5).ToArray();
    }

    private byte[] WriteCoils(byte[] pdu)
    {
        if (pdu.Length < 6)
            return ExceptionPdu(15, 0x03);
        var start = (pdu[1] << 8) | pdu[2];
        var qty = (pdu[3] << 8) | pdu[4];
        for (var i = 0; i < qty; i++)
        {
            var on = (pdu[6 + i / 8] & (1 << (i % 8))) != 0;
            ApplyCoilWrite(start + i, on);
        }

        return [15, pdu[1], pdu[2], pdu[3], pdu[4]];
    }

    private byte[] WriteSingleRegister(byte[] pdu)
    {
        if (pdu.Length < 5)
            return ExceptionPdu(6, 0x03);
        var address = (pdu[1] << 8) | pdu[2];
        var value = (ushort)((pdu[3] << 8) | pdu[4]);
        lock (_gate)
            SetHolding(address, value);
        ApplyRegisterSideEffects();
        return pdu.AsSpan(0, 5).ToArray();
    }

    private byte[] WriteRegisters(byte[] pdu)
    {
        if (pdu.Length < 6)
            return ExceptionPdu(16, 0x03);
        var start = (pdu[1] << 8) | pdu[2];
        var qty = (pdu[3] << 8) | pdu[4];
        lock (_gate)
        {
            for (var i = 0; i < qty; i++)
            {
                var hi = pdu[6 + i * 2];
                var lo = pdu[7 + i * 2];
                SetHolding(start + i, (ushort)((hi << 8) | lo));
            }
        }

        ApplyRegisterSideEffects();
        return [16, pdu[1], pdu[2], pdu[3], pdu[4]];
    }

    private void ApplyCoilWrite(int address, bool value)
    {
        lock (_gate)
            SetCoil(address, value);

        var trigger = Parse(Map.TriggerWrite);
        var complete = Parse(Map.StepComplete);
        var hostHold = string.IsNullOrWhiteSpace(Map.HostHold) ? int.MinValue : Parse(Map.HostHold);
        if (address == trigger)
        {
            if (value)
            {
                float[] parameters;
                int stepId;
                int stepType;
                lock (_gate)
                {
                    stepId = ReadInt32(Parse(Map.StepId));
                    stepType = ReadInt32(Parse(Map.StepType));
                    parameters = new float[16];
                    for (var i = 0; i < 16; i++)
                        parameters[i] = ReadFloat(Parse(Map.Params[i]));
                }

                Station.WritePayload(stepId, stepType, parameters);
                Station.SetTrigger(true);
            }
            else
            {
                Station.SetTrigger(false);
            }
        }
        else if (address == complete && !value)
        {
            Station.ResetComplete();
        }
        else if (address == hostHold)
        {
            Station.SetHostHold(value);
        }
    }

    private void ApplyRegisterSideEffects()
    {
        lock (_gate)
        {
            var errorAddr = Parse(Map.ErrorCode);
            if (ReadInt32(errorAddr) == 0)
            {
                // host reset path writes Error_Code=0 together with coil reset
            }
        }
    }

    private bool GetCoil(int address)
    {
        if ((uint)address >= _coils.Length)
            return false;
        return _coils[address];
    }

    private void SetCoil(int address, bool value)
    {
        if ((uint)address >= _coils.Length)
            return;
        _coils[address] = value;
    }

    private ushort GetHolding(int address)
    {
        if ((uint)address >= _holding.Length)
            return 0;
        return _holding[address];
    }

    private void SetHolding(int address, ushort value)
    {
        if ((uint)address >= _holding.Length)
            return;
        _holding[address] = value;
    }

    private int ReadInt32(int address) =>
        (_holding[address] << 16) | _holding[address + 1];

    private void WriteInt32(int address, int value)
    {
        SetHolding(address, (ushort)(value >> 16));
        SetHolding(address + 1, (ushort)value);
    }

    private float ReadFloat(int address)
    {
        Span<byte> bytes = stackalloc byte[4];
        var hi = GetHolding(address);
        var lo = GetHolding(address + 1);
        bytes[0] = (byte)(hi >> 8);
        bytes[1] = (byte)hi;
        bytes[2] = (byte)(lo >> 8);
        bytes[3] = (byte)lo;
        if (BitConverter.IsLittleEndian)
            bytes.Reverse();
        return BitConverter.ToSingle(bytes);
    }

    private void WriteFloat(int address, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        SetHolding(address, (ushort)((bytes[0] << 8) | bytes[1]));
        SetHolding(address + 1, (ushort)((bytes[2] << 8) | bytes[3]));
    }

    private static int Parse(string address) =>
        int.TryParse(address, out var n) ? n : 0;

    private static byte[] ExceptionPdu(byte function, byte code) =>
        [(byte)(function | 0x80), code];

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _runCts?.Cancel();
        _listener?.Stop();
        if (_accept is not null)
        {
            try { await _accept.ConfigureAwait(false); }
            catch { /* shutdown */ }
        }

        _runCts?.Dispose();
    }
}
