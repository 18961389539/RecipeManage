namespace RecipesManage.Domain.Handshake;

/// <summary>
/// 握手相位的唯一投影：状态机七相位是真相，A/B/C/D 是操作员进度条，
/// 上位机等待/确认/保持是叠加态。批次生命周期（待放行/已放行）不进本列。
/// </summary>
public static class HandshakeView
{
    public const string Held = "Held";
    public const string AwaitingConfirm = "AwaitingConfirm";
    public const string HostWait = "HostWait";
    public const string Separator = " · ";

    /// <summary>与 <see cref="HandshakeStateMachine"/> 注释一致：A 写参、B 应答、C 看门狗、D 归档。</summary>
    public static int? FourStepIndex(string? phase) => Token(phase) switch
    {
        nameof(HandshakePhase.WaitingPlcReady) or nameof(HandshakePhase.WritingParameters) => 0,
        nameof(HandshakePhase.AwaitingPlcAck) => 1,
        nameof(HandshakePhase.StepRunning) or HostWait => 2,
        nameof(HandshakePhase.Completing) or nameof(HandshakePhase.ReadyToAdvance) => 3,
        _ => null
    };

    public static bool IsLifecycleOverlay(string? phase) => Token(phase) is
        "Created" or "Queued" or "Completed" or "Released" or "DispositionRejected" or "Aborted";

    public static bool IsHandshakeToken(string? phase)
    {
        var token = Token(phase);
        return FourStepIndex(token) is not null
               || token is nameof(HandshakePhase.Faulted) or Held or AwaitingConfirm or HostWait;
    }

    public static bool IsSkipSafe(string? phase) => Token(phase) is
        nameof(HandshakePhase.WaitingPlcReady) or Held or AwaitingConfirm or HostWait;

    /// <summary>工步结论不能当握手相位用。历史批次没有车道行、重建车道时才走这个映射。</summary>
    public static string FromStepOutcome(Batches.StepOutcome outcome) => outcome switch
    {
        Batches.StepOutcome.Held => Held,
        Batches.StepOutcome.AwaitingConfirm => AwaitingConfirm,
        Batches.StepOutcome.Faulted => nameof(HandshakePhase.Faulted),
        Batches.StepOutcome.Completed or Batches.StepOutcome.Skipped => nameof(HandshakePhase.ReadyToAdvance),
        _ => nameof(HandshakePhase.WaitingPlcReady)
    };

    /// <summary>
    /// 标题条 / 进度条用的相位：保持与故障看批次状态，其它看车道握手。
    /// 车道上仍保留最后一次状态机相位，便于恢复后继续。
    /// </summary>
    public static string Display(string? batchStatus, string? lanePhase, string? batchPhase)
    {
        if (string.Equals(batchStatus, "Held", StringComparison.Ordinal))
            return Held;
        if (string.Equals(batchStatus, "Faulted", StringComparison.Ordinal))
            return nameof(HandshakePhase.Faulted);
        if (string.Equals(batchStatus, "Completed", StringComparison.Ordinal)
            || string.Equals(batchStatus, "Released", StringComparison.Ordinal)
            || string.Equals(batchStatus, "DispositionRejected", StringComparison.Ordinal))
            return SealCompleted(lanePhase ?? batchPhase);
        if (IsHandshakeToken(lanePhase))
            return Token(lanePhase);
        return batchPhase ?? "";
    }

    public static string SealCompleted(string? current) =>
        Token(current) == nameof(HandshakePhase.Faulted)
            ? nameof(HandshakePhase.Faulted)
            : nameof(HandshakePhase.ReadyToAdvance);

    public static string Token(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var map = Parse(raw);
        if (map.Count == 1)
            return map.Values.First();
        return raw.Trim();
    }

    public static Dictionary<string, string> Parse(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
            return map;

        var parts = text.Contains(Separator, StringComparison.Ordinal)
            ? text.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [text.Trim()];

        foreach (var part in parts)
        {
            var index = part.IndexOf(':');
            if (index <= 0 || index >= part.Length - 1)
                continue;
            var key = part[..index];
            if (key.Contains(' ', StringComparison.Ordinal))
                continue;
            map[key] = part[(index + 1)..];
        }

        return map;
    }
}
