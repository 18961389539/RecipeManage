namespace RecipesManage.Domain.Handshake;

/// <summary>上位机与 PLC 握手阶段。严禁跨阶段盲写。</summary>
public enum HandshakePhase
{
    WaitingPlcReady = 0,
    WritingParameters = 1,
    AwaitingPlcAck = 2,
    StepRunning = 3,
    Completing = 4,
    ReadyToAdvance = 5,
    Faulted = 6
}

public enum HandshakeFaultCode
{
    None = 0,
    PlcReadyTimeout = 1,
    PlcNotIdle = 2,
    PlcReadyLostDuringWrite = 3,
    UnexpectedRunning = 4,
    WriteTimeout = 5,
    AckTimeout = 6,
    PlcReportedError = 7,
    ExecutionTimeout = 8,
    HeartbeatLost = 9,
    ResetTimeout = 10,
    BlindWriteRejected = 11,
    CompleteWithoutRunning = 12,
    HoldAckTimeout = 13,
    WriteVerifyMismatch = 14
}

public sealed record HandshakeFault(HandshakeFaultCode Code, int PlcErrorCode, string Message, DateTimeOffset At);

public sealed record HandshakeWatchdogOptions
{
    public TimeSpan ReadyWaitTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ResetTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan IdleSettleTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HoldAckTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public static HandshakeWatchdogOptions FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new HandshakeWatchdogOptions();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new HandshakeWatchdogOptions
            {
                ReadyWaitTimeout = Seconds(root, "readyWaitSeconds", 15),
                WriteTimeout = Seconds(root, "writeTimeoutSeconds", 5),
                AckTimeout = Seconds(root, "ackTimeoutSeconds", 8),
                HeartbeatTimeout = Seconds(root, "heartbeatTimeoutSeconds", 3),
                ResetTimeout = Seconds(root, "resetTimeoutSeconds", 8),
                IdleSettleTimeout = Seconds(root, "idleSettleSeconds", 10),
                HoldAckTimeout = Seconds(root, "holdAckSeconds", 8)
            };
        }
        catch
        {
            return new HandshakeWatchdogOptions();
        }
    }

    private static TimeSpan Seconds(System.Text.Json.JsonElement root, string name, double fallback)
    {
        if (root.TryGetProperty(name, out var prop) && prop.TryGetDouble(out var value) && value > 0)
            return TimeSpan.FromSeconds(value);
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        if (root.TryGetProperty(pascal, out prop) && prop.TryGetDouble(out value) && value > 0)
            return TimeSpan.FromSeconds(value);
        return TimeSpan.FromSeconds(fallback);
    }
}

/// <summary>PLC 回传及回读到的握手变量。</summary>
public sealed record PlcInboundSignals(
    bool PlcReady,
    bool StepRunning,
    bool StepComplete,
    bool StepError,
    int ErrorCode,
    uint Heartbeat,
    bool TriggerWriteEcho,
    bool PlcHeld = false,
    bool HostHoldEcho = false);

/// <summary>写参后从 PLC 回读的 Step_ID / Step_Type / Params，用于禁止未校验盲触发。</summary>
public sealed record PlcStepPayload(int StepId, int StepType, IReadOnlyList<float> Parameters);

public static class PlcWriteVerify
{
    public const float Epsilon = 0.05f;

    public static bool Matches(
        int stepId,
        int stepType,
        IReadOnlyList<float> written,
        PlcStepPayload echo,
        out string mismatch)
    {
        if (echo.StepId != stepId)
        {
            mismatch = $"回读 Step_ID={echo.StepId} 与写入 {stepId} 不一致，拒绝 Trigger_Write。";
            return false;
        }

        if (echo.StepType != stepType)
        {
            mismatch = $"回读 Step_Type={echo.StepType} 与写入 {stepType} 不一致，拒绝 Trigger_Write。";
            return false;
        }

        var count = Math.Min(written.Count, echo.Parameters.Count);
        for (var i = 0; i < count; i++)
        {
            if (Math.Abs(written[i] - echo.Parameters[i]) > Epsilon)
            {
                mismatch = $"回读 Param[{i}]={echo.Parameters[i]} 与写入 {written[i]} 不一致，拒绝 Trigger_Write。";
                return false;
            }
        }

        mismatch = "";
        return true;
    }
}

public sealed record HandshakeWorkContext(
    int StepId,
    int StepType,
    IReadOnlyList<float> Parameters,
    TimeSpan ExecutionTimeout,
    TimeSpan? ProcessDuration = null);

public abstract record HandshakeAction(string Kind);

public sealed record WriteStepPayloadAction(int StepId, int StepType, IReadOnlyList<float> Parameters)
    : HandshakeAction("WriteStepPayload");

public sealed record AssertTriggerWriteAction(bool Value) : HandshakeAction("AssertTriggerWrite");

public sealed record AssertHostHoldAction(bool Value) : HandshakeAction("AssertHostHold");

public sealed record ResetCompleteAction() : HandshakeAction("ResetComplete");

public sealed record ArchiveMeasurementsAction() : HandshakeAction("ArchiveMeasurements");

public sealed record AdvanceStepAction() : HandshakeAction("AdvanceStep");

public sealed record RaiseFaultAction(HandshakeFault Fault) : HandshakeAction("RaiseFault");
