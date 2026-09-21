namespace RecipesManage.Domain.Handshake;

/// <summary>
/// 上位机 ↔ PLC 四步闭环握手状态机（Anti-Blind-Write）。
/// A: PLC_Ready 后写缓存并置 Trigger_Write；
/// B: 等待 Step_Running；
/// C: 看门狗监控运行与心跳；
/// D: Step_Complete 后归档实测、复位完成信号、步进。
/// </summary>
public sealed class HandshakeStateMachine
{
    private readonly HandshakeWatchdogOptions _watchdog;
    private DateTimeOffset _phaseEnteredAt;
    private DateTimeOffset _lastHeartbeatAt;
    private uint _lastHeartbeat;
    private bool _writeIssued;
    private bool _paramsWritten;
    private bool _triggerIssued;
    private bool _archiveIssued;
    private bool _archiveDone;
    private bool _resetIssued;

    public HandshakePhase Phase { get; private set; } = HandshakePhase.WaitingPlcReady;
    public HandshakeFault? Fault { get; private set; }

    public HandshakeStateMachine(HandshakeWatchdogOptions? watchdog = null, DateTimeOffset? now = null)
    {
        _watchdog = watchdog ?? new HandshakeWatchdogOptions();
        _phaseEnteredAt = now ?? DateTimeOffset.UtcNow;
        _lastHeartbeatAt = _phaseEnteredAt;
    }

    public IReadOnlyList<HandshakeAction> Tick(
        PlcInboundSignals inbound,
        HandshakeWorkContext work,
        DateTimeOffset now)
    {
        if (Phase == HandshakePhase.Faulted)
            return [];

        if (inbound.StepError && Phase is not HandshakePhase.ReadyToAdvance)
            return FaultAndRaise(HandshakeFaultCode.PlcReportedError, inbound.ErrorCode, now,
                $"PLC 报错，Error_Code={inbound.ErrorCode}。");

        return Phase switch
        {
            HandshakePhase.WaitingPlcReady => TickWaitingReady(inbound, work, now),
            HandshakePhase.WritingParameters => TickWriting(inbound, work, now),
            HandshakePhase.AwaitingPlcAck => TickAwaitingAck(inbound, now),
            HandshakePhase.StepRunning => TickRunning(inbound, work, now),
            HandshakePhase.Completing => TickCompleting(inbound, now),
            HandshakePhase.ReadyToAdvance => [],
            _ => []
        };
    }

    public void NotifyParametersWritten(DateTimeOffset now)
    {
        if (Phase != HandshakePhase.WritingParameters)
        {
            EnterFault(HandshakeFaultCode.BlindWriteRejected, 0, now, "禁止在非 WritingParameters 阶段确认写参（盲写拒绝）。");
            return;
        }
        _paramsWritten = true;
        _phaseEnteredAt = now;
    }

    public void NotifyWriteVerifyFailed(DateTimeOffset now, string message)
    {
        EnterFault(HandshakeFaultCode.WriteVerifyMismatch, 0, now, message);
    }

    public void NotifyTriggerAsserted()
    {
        if (Phase != HandshakePhase.AwaitingPlcAck)
        {
            EnterFault(HandshakeFaultCode.BlindWriteRejected, 0, DateTimeOffset.UtcNow,
                "Trigger_Write 只能在写参完成进入 AwaitingPlcAck 后置位（盲写拒绝）。");
            return;
        }
        _triggerIssued = true;
    }

    public void NotifyArchiveCompleted()
    {
        if (Phase != HandshakePhase.Completing)
            throw new InvalidOperationException("实测归档只能在 Completing 阶段进行。");
        _archiveDone = true;
    }

    public void NotifyResetIssued() => _resetIssued = true;

    public double? RemainingSeconds(HandshakeWorkContext work, DateTimeOffset now)
    {
        var limit = Phase switch
        {
            HandshakePhase.WaitingPlcReady => _watchdog.ReadyWaitTimeout,
            HandshakePhase.WritingParameters => _watchdog.WriteTimeout,
            HandshakePhase.AwaitingPlcAck => _watchdog.AckTimeout,
            HandshakePhase.StepRunning => work.ProcessDuration ?? work.ExecutionTimeout,
            HandshakePhase.Completing => _watchdog.ResetTimeout,
            _ => (TimeSpan?)null
        };
        if (limit is null)
            return null;
        return Math.Max(0, (limit.Value - Elapsed(now)).TotalSeconds);
    }

    public static HandshakeStateMachine ResumeFromPlc(
        PlcInboundSignals inbound,
        HandshakeWatchdogOptions? watchdog,
        DateTimeOffset now)
    {
        var machine = new HandshakeStateMachine(watchdog, now);
        if (inbound.StepError)
        {
            machine.EnterFault(HandshakeFaultCode.PlcReportedError, inbound.ErrorCode, now, "恢复会话时 PLC 已处于故障。");
            return machine;
        }

        if (inbound.StepRunning)
        {
            machine.Phase = HandshakePhase.StepRunning;
            machine._lastHeartbeat = inbound.Heartbeat;
            machine._lastHeartbeatAt = now;
            machine._writeIssued = true;
            machine._paramsWritten = true;
            machine._triggerIssued = true;
            return machine;
        }

        if (inbound.StepComplete)
        {
            machine.Phase = HandshakePhase.Completing;
            machine._writeIssued = true;
            machine._paramsWritten = true;
            machine._triggerIssued = true;
            return machine;
        }

        if (inbound.TriggerWriteEcho && !inbound.PlcReady)
        {
            machine.Phase = HandshakePhase.AwaitingPlcAck;
            machine._writeIssued = true;
            machine._paramsWritten = true;
            machine._triggerIssued = true;
            return machine;
        }

        machine.Phase = HandshakePhase.WaitingPlcReady;
        return machine;
    }

    private List<HandshakeAction> TickWaitingReady(PlcInboundSignals inbound, HandshakeWorkContext work, DateTimeOffset now)
    {
        if (inbound.StepRunning || inbound.StepComplete)
        {
            if (Elapsed(now) > _watchdog.IdleSettleTimeout)
                return FaultAndRaise(HandshakeFaultCode.PlcNotIdle, inbound.ErrorCode, now,
                    "PLC 未回到空闲（仍 Running/Complete），拒绝写入。");
            return [];
        }

        if (!inbound.PlcReady)
        {
            if (Elapsed(now) > _watchdog.ReadyWaitTimeout)
                return FaultAndRaise(HandshakeFaultCode.PlcReadyTimeout, 0, now, "等待 PLC_Ready 超时。");
            return [];
        }

        if (inbound.TriggerWriteEcho)
            return [];

        if (_writeIssued)
            return [];

        _writeIssued = true;
        Enter(HandshakePhase.WritingParameters, now);
        return [new WriteStepPayloadAction(work.StepId, work.StepType, work.Parameters)];
    }

    private List<HandshakeAction> TickWriting(PlcInboundSignals inbound, HandshakeWorkContext work, DateTimeOffset now)
    {
        if (inbound.StepRunning)
            return FaultAndRaise(HandshakeFaultCode.UnexpectedRunning, 0, now, "写参期间 PLC 意外进入 Running。");

        if (inbound.TriggerWriteEcho && !_paramsWritten)
            return FaultAndRaise(HandshakeFaultCode.BlindWriteRejected, 0, now, "写参前 Trigger_Write 已置位，拒绝盲写。");

        if (!inbound.PlcReady)
            return FaultAndRaise(HandshakeFaultCode.PlcReadyLostDuringWrite, 0, now, "写参期间 PLC_Ready 丢失，中止写入。");

        if (!_paramsWritten)
        {
            if (Elapsed(now) > _watchdog.WriteTimeout)
                return FaultAndRaise(HandshakeFaultCode.WriteTimeout, 0, now, "参数写入超时。");
            if (!_writeIssued)
            {
                _writeIssued = true;
                return [new WriteStepPayloadAction(work.StepId, work.StepType, work.Parameters)];
            }
            return [];
        }

        Enter(HandshakePhase.AwaitingPlcAck, now);
        return [new AssertTriggerWriteAction(true)];
    }

    private List<HandshakeAction> TickAwaitingAck(PlcInboundSignals inbound, DateTimeOffset now)
    {
        if (!_triggerIssued)
            return [];

        if (inbound.StepRunning)
        {
            Enter(HandshakePhase.StepRunning, now);
            _lastHeartbeat = inbound.Heartbeat;
            _lastHeartbeatAt = now;
            return [];
        }

        if (inbound.StepComplete && !inbound.StepRunning)
            return FaultAndRaise(HandshakeFaultCode.CompleteWithoutRunning, 0, now,
                "未观察到 Step_Running 却收到 Step_Complete，握手时序非法。");

        if (Elapsed(now) > _watchdog.AckTimeout)
            return FaultAndRaise(HandshakeFaultCode.AckTimeout, 0, now, "PLC 未在时限内以 Step_Running 应答。");

        return [];
    }

    private List<HandshakeAction> TickRunning(PlcInboundSignals inbound, HandshakeWorkContext work, DateTimeOffset now)
    {
        if (inbound.Heartbeat != _lastHeartbeat)
        {
            _lastHeartbeat = inbound.Heartbeat;
            _lastHeartbeatAt = now;
        }
        else if (now - _lastHeartbeatAt > _watchdog.HeartbeatTimeout)
        {
            return FaultAndRaise(HandshakeFaultCode.HeartbeatLost, 0, now, "PLC 心跳丢失，判定断网或任务卡死。");
        }

        if (inbound.StepComplete && !inbound.StepRunning)
        {
            Enter(HandshakePhase.Completing, now);
            _archiveIssued = true;
            return [new ArchiveMeasurementsAction()];
        }

        if (Elapsed(now) > work.ExecutionTimeout)
            return FaultAndRaise(HandshakeFaultCode.ExecutionTimeout, 0, now, "工步执行超过看门狗时限。");

        return [];
    }

    private List<HandshakeAction> TickCompleting(PlcInboundSignals inbound, DateTimeOffset now)
    {
        var actions = new List<HandshakeAction>();

        if (!_archiveIssued)
        {
            _archiveIssued = true;
            actions.Add(new ArchiveMeasurementsAction());
            return actions;
        }

        if (!_archiveDone)
            return actions;

        if (!_resetIssued)
        {
            _resetIssued = true;
            actions.Add(new ResetCompleteAction());
            actions.Add(new AssertTriggerWriteAction(false));
            return actions;
        }

        if (inbound.PlcReady && !inbound.StepComplete && !inbound.StepRunning)
        {
            Enter(HandshakePhase.ReadyToAdvance, now);
            actions.Add(new AdvanceStepAction());
            return actions;
        }

        if (Elapsed(now) > _watchdog.ResetTimeout)
            return FaultAndRaise(HandshakeFaultCode.ResetTimeout, 0, now, "复位完成信号后 PLC 未回到 Ready。");

        return actions;
    }

    private void Enter(HandshakePhase phase, DateTimeOffset now)
    {
        Phase = phase;
        _phaseEnteredAt = now;
    }

    private List<HandshakeAction> FaultAndRaise(HandshakeFaultCode code, int plcError, DateTimeOffset now, string message)
    {
        EnterFault(code, plcError, now, message);
        return [new RaiseFaultAction(Fault!)];
    }

    private void EnterFault(HandshakeFaultCode code, int plcError, DateTimeOffset now, string message)
    {
        Phase = HandshakePhase.Faulted;
        Fault = new HandshakeFault(code, plcError, message, now);
        _phaseEnteredAt = now;
    }

    private TimeSpan Elapsed(DateTimeOffset now) => now - _phaseEnteredAt;
}
