namespace Wire.Resilience;

/// <summary>What to execute: a label, the circuit-breaker key, the policy, and whether retrying is safe.</summary>
public sealed record WireOperation(string Name, string EndpointKey, ResiliencePolicy Policy, bool Retryable = true);

/// <summary>A result that should be retried (e.g. HTTP 503), optionally with a server-provided delay.</summary>
public sealed record TransientFailure(string Reason, TimeSpan? RetryAfter = null);

/// <summary>The single resilience surface used by every protocol adapter.</summary>
public interface IResilienceExecutor
{
    /// <param name="classifyResult">Return non-null to treat a returned value as a transient failure. If retries run out, the last value is still returned.</param>
    /// <param name="isTransient">Decides whether an exception is retryable. Timeouts are always transient. Default: <see cref="HttpRequestException"/>, <see cref="IOException"/>.</param>
    Task<T> ExecuteAsync<T>(
        WireOperation operation,
        Func<CancellationToken, Task<T>> action,
        Func<T, TransientFailure?>? classifyResult = null,
        Func<Exception, bool>? isTransient = null,
        CancellationToken cancellationToken = default);

    CircuitState GetCircuitState(string endpointKey);
}
