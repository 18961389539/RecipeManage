using System.Net;
using System.Net.Sockets;
using RecipesManage.Domain.Equipment;

namespace RecipesManage.Infrastructure.Plc;

/// <summary>
/// 本机 S7 ISO-on-TCP 从站：把 IoTClient SiemensClient 的 DB 读写映射到
/// <see cref="SimulatedPlcStation"/> 四步握手。默认点表即 HandshakeTagMap 的 DB10 布局。
/// </summary>
public sealed class SiemensS7HandshakeSlave : IAsyncDisposable
{
    public SimulatedPlcStation Station { get; } = new();
    public HandshakeTagMap Map { get; } = new();
    public int Port { get; private set; }
    public Guid? BoundEquipmentId { get; private set; }

    private readonly object _gate = new();
    private readonly byte[] _db = new byte[256];
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
                try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
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
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var tpkt = new byte[4];
                    await ReadExactAsync(stream, tpkt, 4, ct).ConfigureAwait(false);
                    var length = (tpkt[2] << 8) | tpkt[3];
                    if (length < 7 || length > 1024)
                        return;
                    var rest = new byte[length - 4];
                    await ReadExactAsync(stream, rest, rest.Length, ct).ConfigureAwait(false);
                    var pdu = new byte[length];
                    Buffer.BlockCopy(tpkt, 0, pdu, 0, 4);
                    Buffer.BlockCopy(rest, 0, pdu, 4, rest.Length);
                    var reply = HandlePdu(pdu);
                    if (reply.Length > 0)
                        await stream.WriteAsync(reply, ct).ConfigureAwait(false);
                }
            }
            catch (EndOfStreamException) { }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }

    private byte[] HandlePdu(byte[] pdu)
    {
        if (pdu.Length >= 6 && pdu[5] == 0xE0)
            return CotpConnectConfirm(pdu);

        if (pdu.Length >= 18 && pdu[7] == 0x32 && pdu[17] == 0xF0)
            return SetupAck(pdu);

        if (pdu.Length >= 19 && pdu[7] == 0x32 && pdu[17] == 0x04)
            return HandleRead(pdu);

        if (pdu.Length >= 19 && pdu[7] == 0x32 && pdu[17] == 0x05)
            return HandleWrite(pdu);

        return [];
    }

    private static byte[] CotpConnectConfirm(byte[] request)
    {
        var reply = (byte[])request.Clone();
        reply[5] = 0xD0;
        reply[6] = request[8];
        reply[7] = request[9];
        reply[8] = request[8];
        reply[9] = request[9];
        return reply;
    }

    private static byte[] SetupAck(byte[] request)
    {
        return
        [
            0x03, 0x00, 0x00, 0x1B,
            0x02, 0xF0, 0x80,
            0x32, 0x03, 0x00, 0x00,
            request[11], request[12],
            0x00, 0x08, 0x00, 0x00, 0x00, 0x00,
            0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0xF0
        ];
    }

    private byte[] HandleRead(byte[] request)
    {
        PublishInbound();
        var items = request[18];
        if (items < 1)
            return [];
        var first = ParseItem(request, 19);
        byte[] payload;
        lock (_gate)
        {
            if (first.Bit)
                payload = [GetBit(first.ByteOffset, first.BitIndex) ? (byte)0x01 : (byte)0x00];
            else
            {
                payload = new byte[Math.Max(1, first.ByteLength)];
                Array.Copy(_db, first.ByteOffset, payload, 0, Math.Min(payload.Length, _db.Length - first.ByteOffset));
            }
        }

        var body = 23 + payload.Length;
        var reply = new byte[body];
        reply[0] = 0x03;
        reply[1] = 0x00;
        reply[2] = (byte)(body >> 8);
        reply[3] = (byte)body;
        reply[4] = 0x02;
        reply[5] = 0xF0;
        reply[6] = 0x80;
        reply[7] = 0x32;
        reply[8] = 0x03;
        reply[11] = request[11];
        reply[12] = request[12];
        reply[13] = 0x00;
        reply[14] = 0x02;
        var dataLen = 4 + payload.Length;
        reply[15] = (byte)(dataLen >> 8);
        reply[16] = (byte)dataLen;
        reply[17] = 0x04;
        reply[18] = 0x01;
        reply[19] = 0xFF;
        reply[20] = first.Bit ? (byte)0x03 : (byte)0x04;
        var bitLen = first.Bit ? 1 : payload.Length * 8;
        reply[21] = (byte)(bitLen >> 8);
        reply[22] = (byte)bitLen;
        Buffer.BlockCopy(payload, 0, reply, 23, payload.Length);
        return reply;
    }

    private byte[] HandleWrite(byte[] request)
    {
        var items = request[18];
        var parsed = new List<S7Item>(items);
        for (var i = 0; i < items; i++)
            parsed.Add(ParseItem(request, 19 + i * 12));

        var index = 19 + items * 12;
        for (var i = 0; i < items && index + 4 <= request.Length; i++)
        {
            var item = parsed[i];
            var transport = request[index + 1];
            var isBit = transport == 0x03 || item.Bit;
            var declared = (request[index + 2] << 8) | request[index + 3];
            index += 4;
            if (isBit)
            {
                var on = index < request.Length && request[index] != 0;
                ApplyBitWrite(item.ByteOffset, item.BitIndex, on);
                index += i >= items - 1 ? 1 : 2;
            }
            else
            {
                var n = item.ByteLength > 0 ? item.ByteLength : Math.Max(1, (declared + 7) / 8);
                lock (_gate)
                {
                    for (var b = 0; b < n && index + b < request.Length && item.ByteOffset + b < _db.Length; b++)
                        _db[item.ByteOffset + b] = request[index + b];
                }

                index += n;
            }
        }

        ApplyRegisterSideEffects();

        var reply = new byte[21 + items];
        reply[0] = 0x03;
        reply[1] = 0x00;
        reply[2] = (byte)(reply.Length >> 8);
        reply[3] = (byte)reply.Length;
        reply[4] = 0x02;
        reply[5] = 0xF0;
        reply[6] = 0x80;
        reply[7] = 0x32;
        reply[8] = 0x03;
        reply[11] = request[11];
        reply[12] = request[12];
        reply[17] = 0x05;
        reply[18] = items;
        for (var i = 0; i < items; i++)
            reply[21 + i] = 0xFF;
        return reply;
    }

    private void PublishInbound()
    {
        var signals = Station.ReadSignals();
        var measured = Station.ReadMeasured();
        lock (_gate)
        {
            SetBitAddr(Map.PlcReady, signals.PlcReady);
            SetBitAddr(Map.StepRunning, signals.StepRunning);
            SetBitAddr(Map.StepComplete, signals.StepComplete);
            SetBitAddr(Map.StepError, signals.StepError);
            SetBitAddr(Map.TriggerWrite, signals.TriggerWriteEcho);
            if (!string.IsNullOrWhiteSpace(Map.PlcHeld))
                SetBitAddr(Map.PlcHeld, signals.PlcHeld);
            if (!string.IsNullOrWhiteSpace(Map.HostHold))
                SetBitAddr(Map.HostHold, signals.HostHoldEcho);
            WriteInt32Addr(Map.ErrorCode, signals.ErrorCode);
            WriteInt32Addr(Map.Heartbeat, (int)signals.Heartbeat);
            foreach (var (name, address) in Map.Measured)
            {
                if (measured.TryGetValue(name, out var value))
                    WriteFloatAddr(address, (float)value);
            }
        }
    }

    private void ApplyBitWrite(int byteOffset, int bit, bool on)
    {
        lock (_gate)
            SetBit(byteOffset, bit, on);

        var trigger = ParseAddr(Map.TriggerWrite);
        var complete = ParseAddr(Map.StepComplete);
        var hostHold = string.IsNullOrWhiteSpace(Map.HostHold) ? default : ParseAddr(Map.HostHold);
        if (byteOffset == trigger.ByteOffset && bit == trigger.BitIndex)
        {
            if (on)
            {
                float[] parameters;
                int stepId;
                int stepType;
                lock (_gate)
                {
                    stepId = ReadInt32Addr(Map.StepId);
                    stepType = ReadInt32Addr(Map.StepType);
                    parameters = new float[16];
                    for (var i = 0; i < 16 && i < Map.Params.Count; i++)
                        parameters[i] = ReadFloatAddr(Map.Params[i]);
                }

                Station.WritePayload(stepId, stepType, parameters);
                Station.SetTrigger(true);
            }
            else
            {
                Station.SetTrigger(false);
            }
        }
        else if (byteOffset == complete.ByteOffset && bit == complete.BitIndex && !on)
        {
            Station.ResetComplete();
        }
        else if (!string.IsNullOrWhiteSpace(Map.HostHold) &&
                 byteOffset == hostHold.ByteOffset && bit == hostHold.BitIndex)
        {
            Station.SetHostHold(on);
        }
    }

    private void ApplyRegisterSideEffects()
    {
        // dword writes of Error_Code=0 accompany ResetComplete coil/bit path
    }

    private void SetBitAddr(string address, bool value)
    {
        var a = ParseAddr(address);
        SetBit(a.ByteOffset, a.BitIndex, value);
    }

    private void WriteInt32Addr(string address, int value)
    {
        var a = ParseAddr(address);
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        Array.Copy(bytes, 0, _db, a.ByteOffset, 4);
    }

    private int ReadInt32Addr(string address)
    {
        var a = ParseAddr(address);
        var bytes = new byte[4];
        Array.Copy(_db, a.ByteOffset, bytes, 0, 4);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToInt32(bytes);
    }

    private void WriteFloatAddr(string address, float value)
    {
        var a = ParseAddr(address);
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        Array.Copy(bytes, 0, _db, a.ByteOffset, 4);
    }

    private float ReadFloatAddr(string address)
    {
        var a = ParseAddr(address);
        var bytes = new byte[4];
        Array.Copy(_db, a.ByteOffset, bytes, 0, 4);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToSingle(bytes);
    }

    private bool GetBit(int byteOffset, int bit)
    {
        if ((uint)byteOffset >= _db.Length)
            return false;
        return (_db[byteOffset] & (1 << bit)) != 0;
    }

    private void SetBit(int byteOffset, int bit, bool on)
    {
        if ((uint)byteOffset >= _db.Length)
            return;
        if (on)
            _db[byteOffset] |= (byte)(1 << bit);
        else
            _db[byteOffset] &= (byte)~(1 << bit);
    }

    private static S7Item ParseItem(byte[] request, int offset)
    {
        var transport = request[offset + 3];
        var byteLength = (request[offset + 4] << 8) | request[offset + 5];
        var bitAddr = (request[offset + 9] << 16) | (request[offset + 10] << 8) | request[offset + 11];
        return new S7Item(bitAddr / 8, bitAddr % 8, Math.Max(1, byteLength), transport == 0x01);
    }

    private static S7Item ParseAddr(string address)
    {
        address = address.Trim().ToUpperInvariant();
        var parts = address.Split('.');
        var start = parts[0].StartsWith("DB", StringComparison.Ordinal) ? 1 : 0;
        var byteOffset = int.Parse(parts[start]);
        var bit = parts.Length > start + 1 ? int.Parse(parts[start + 1]) : 0;
        return new S7Item(byteOffset, bit, 4, parts.Length > start + 1);
    }

    private readonly record struct S7Item(int ByteOffset, int BitIndex, int ByteLength, bool Bit);

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
