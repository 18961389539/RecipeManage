using RecipesManage.Domain.Handshake;

namespace RecipesManage.Simulation;

public sealed class SimulatedPlcRack
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SimulatedPlcStation> _stations = new();
    private readonly TimeProvider _clock;

    /// <param name="clock">与调度器共用同一实例：测试注入 FakeTimeProvider 时，PLC 侧的保温计时
    /// 跟调度器的窗口数学跑在同一套虚拟时间上，两套时间不再脱钩。</param>
    public SimulatedPlcRack(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public SimulatedPlcStation Get(Guid equipmentId) =>
        _stations.GetOrAdd(equipmentId, _ => new SimulatedPlcStation(_clock));
}

public sealed class SimulatedPlcStation
{
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private int _stepId;
    private int _stepType;
    private readonly float[] _params = new float[16];
    private bool _triggerWrite;
    private bool _plcReady = true;
    private bool _stepRunning;
    private bool _stepComplete;
    private bool _stepError;
    private bool _hostHold;
    private bool _plcHeld;
    private int _errorCode;
    private uint _heartbeat;
    private TimeSpan _runDuration = TimeSpan.FromSeconds(2);
    private DateTime _runSegmentStarted;
    private TimeSpan _remaining;
    private readonly Dictionary<string, double> _measured = new()
    {
        ["Temperature"] = 25,
        ["Pressure"] = 1.01,
        ["HoldTime"] = 0
    };
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private string _fault = "None";

    public SimulatedPlcStation(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public string FaultMode
    {
        get { lock (_gate) return _fault; }
    }

    public void InjectFault(string mode)
    {
        lock (_gate)
        {
            _fault = string.IsNullOrWhiteSpace(mode) ? "None" : mode.Trim();
            if (_fault.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                _plcReady = true;
                _stepRunning = false;
                _stepComplete = false;
                _stepError = false;
                _errorCode = 0;
                _triggerWrite = false;
                _hostHold = false;
                _plcHeld = false;
            }
            else if (_fault.Equals("HoldNotReady", StringComparison.OrdinalIgnoreCase))
            {
                _plcReady = false;
                _stepRunning = false;
                _stepComplete = false;
                _stepError = false;
            }
        }
    }

    public PlcInboundSignals ReadSignals()
    {
        lock (_gate)
        {
            if (_fault.Equals("HoldNotReady", StringComparison.OrdinalIgnoreCase))
                return new PlcInboundSignals(false, false, false, false, 0, _heartbeat, _triggerWrite, _plcHeld, _hostHold);
            if (_fault.Equals("DropHeartbeat", StringComparison.OrdinalIgnoreCase) && _stepRunning)
                return new PlcInboundSignals(_plcReady, _stepRunning, _stepComplete, _stepError, _errorCode, _heartbeat, _triggerWrite, _plcHeld, _hostHold);
            return new PlcInboundSignals(_plcReady, _stepRunning, _stepComplete, _stepError, _errorCode, _heartbeat, _triggerWrite, _plcHeld, _hostHold);
        }
    }

    public IReadOnlyDictionary<string, double> ReadMeasured()
    {
        lock (_gate)
            return new Dictionary<string, double>(_measured);
    }

    public void WritePayload(int stepId, int stepType, IReadOnlyList<float> parameters)
    {
        lock (_gate)
        {
            _stepId = stepId;
            _stepType = stepType;
            for (var i = 0; i < _params.Length; i++)
                _params[i] = i < parameters.Count ? parameters[i] : 0f;
        }
    }

    public PlcStepPayload ReadPayload()
    {
        lock (_gate)
        {
            var copy = (float[])_params.Clone();
            if (_fault.Equals("CorruptEcho", StringComparison.OrdinalIgnoreCase))
                return new PlcStepPayload(_stepId + 1, _stepType, copy);
            return new PlcStepPayload(_stepId, _stepType, copy);
        }
    }

    public void SetTrigger(bool value)
    {
        lock (_gate)
        {
            _triggerWrite = value;
            if (!value)
                return;

            if (!_plcReady || _stepRunning || _plcHeld)
            {
                _stepError = true;
                _errorCode = 0x10;
                return;
            }

            if (_fault.Equals("NoAck", StringComparison.OrdinalIgnoreCase))
            {
                _plcReady = false;
                _stepRunning = false;
                _stepComplete = false;
                return;
            }

            if (_fault.Equals("StepError", StringComparison.OrdinalIgnoreCase))
            {
                _plcReady = false;
                _stepError = true;
                _errorCode = 0x21;
                return;
            }

            _plcReady = false;
            _stepComplete = false;
            _stepError = false;
            _errorCode = 0;
            _stepRunning = true;
            _heartbeat++;
            StartRun_NoLock(null);
        }
    }

    public void SetHostHold(bool value)
    {
        lock (_gate)
        {
            _hostHold = value;
            if (value)
            {
                if (!_stepRunning && !_plcHeld)
                    return;
                var elapsed = _clock.GetUtcNow().UtcDateTime - _runSegmentStarted;
                _remaining = _runDuration - elapsed;
                if (_remaining < TimeSpan.Zero)
                    _remaining = TimeSpan.Zero;
                _runCts?.Cancel();
                _stepRunning = false;
                _plcHeld = true;
                _plcReady = false;
                return;
            }

            if (!_plcHeld)
                return;

            _plcHeld = false;
            if (_stepComplete)
                return;
            if (_remaining <= TimeSpan.FromMilliseconds(120))
            {
                _stepComplete = true;
                _stepRunning = false;
                return;
            }

            _plcReady = false;
            _stepRunning = true;
            StartRun_NoLock(_remaining);
        }
    }

    public void ResetComplete()
    {
        lock (_gate)
        {
            _runCts?.Cancel();
            _stepComplete = false;
            _stepError = false;
            _errorCode = 0;
            _stepRunning = false;
            _triggerWrite = false;
            _hostHold = false;
            _plcHeld = false;
            _plcReady = true;
        }
    }

    private void StartRun_NoLock(TimeSpan? remaining)
    {
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        var stepType = _stepType;
        var targetTemp = _params[0] > 1 ? _params[0] : 100f;
        var duration = remaining ?? ResolveRunDuration(_params, stepType);
        _runDuration = duration;
        _remaining = duration;
        _runSegmentStarted = _clock.GetUtcNow().UtcDateTime;
        var startTemp = _measured.GetValueOrDefault("Temperature", 25d);

        _runTask = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var elapsed = _clock.GetUtcNow().UtcDateTime - _runSegmentStarted;
                    var ratio = Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
                    lock (_gate)
                    {
                        if (!_fault.Equals("DropHeartbeat", StringComparison.OrdinalIgnoreCase))
                            _heartbeat++;
                        _remaining = duration - elapsed;
                        _measured["Temperature"] = startTemp + (targetTemp - startTemp) * ratio;
                        _measured["Pressure"] = 1.01 + Math.Sin(elapsed.TotalSeconds) * 0.05;
                        _measured["HoldTime"] = elapsed.TotalSeconds;
                    }

                    if (elapsed >= duration)
                    {
                        lock (_gate)
                        {
                            _stepRunning = false;
                            _stepComplete = true;
                            _plcHeld = false;
                            _measured["Temperature"] = targetTemp;
                        }
                        return;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(200), _clock, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // paused or aborted
            }
        }, ct);
    }

    /// <summary>
    /// 优先使用 Param[15] 工艺时长（上位机写入，避开斜率等占用 Param[1]）。
    /// 未写则回退 Param[1] 0.4–120s，以兼容历史仿真测例。
    /// </summary>
    internal static TimeSpan ResolveRunDuration(float[] p, int stepType)
    {
        if (p.Length > 15 && p[15] > 0.4f && p[15] < 7200f)
            return TimeSpan.FromSeconds(p[15]);
        if (p.Length > 1 && p[1] > 0.4f && p[1] < 120f)
            return TimeSpan.FromSeconds(p[1]);
        if (stepType == 7)
            return TimeSpan.FromSeconds(1);
        return TimeSpan.FromSeconds(2);
    }
}
