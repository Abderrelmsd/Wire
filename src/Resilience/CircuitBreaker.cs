namespace Wire.Resilience;

/// <summary>Consecutive-failure breaker: Closed → Open (threshold) → HalfOpen (one probe after the break) → Closed/Open.</summary>
internal sealed class CircuitBreaker(string key, ResiliencePolicy policy, TimeProvider time, Action<string, CircuitState, CircuitState> onChange)
{
    private readonly Lock _gate = new();
    private CircuitState _state = CircuitState.Closed;
    private int _failures;
    private DateTimeOffset _openedAt;
    private bool _probeInFlight;

    public CircuitState State { get { lock (_gate) return _state; } }

    /// <summary>True if a call may proceed; otherwise <paramref name="retryAt"/> says when the circuit may next admit a probe.</summary>
    public bool TryAcquire(out DateTimeOffset retryAt)
    {
        retryAt = default;
        if (policy.CircuitFailureThreshold <= 0) return true;
        lock (_gate)
        {
            switch (_state)
            {
                case CircuitState.Closed:
                    return true;
                case CircuitState.Open:
                    retryAt = _openedAt + policy.CircuitBreakDuration;
                    if (time.GetUtcNow() < retryAt) return false;
                    Transition(CircuitState.HalfOpen);
                    _probeInFlight = true;
                    return true;
                default:
                    if (_probeInFlight) { retryAt = time.GetUtcNow() + TimeSpan.FromSeconds(1); return false; }
                    _probeInFlight = true;
                    return true;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _failures = 0;
            _probeInFlight = false;
            if (_state != CircuitState.Closed) Transition(CircuitState.Closed);
        }
    }

    public void RecordFailure()
    {
        if (policy.CircuitFailureThreshold <= 0) return;
        lock (_gate)
        {
            _probeInFlight = false;
            if (_state == CircuitState.HalfOpen || ++_failures >= policy.CircuitFailureThreshold)
            {
                _openedAt = time.GetUtcNow();
                if (_state != CircuitState.Open) Transition(CircuitState.Open);
            }
        }
    }

    /// <summary>The attempt ended without a verdict (caller cancelled): free a half-open probe slot.</summary>
    public void Release() { lock (_gate) _probeInFlight = false; }

    private void Transition(CircuitState to)
    {
        var from = _state;
        _state = to;
        onChange(key, from, to);
    }
}
