using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace Wire.Resilience;

internal sealed class ResilienceExecutor(
    IEnumerable<IWireObserver> observers,
    TimeProvider time,
    ILogger<ResilienceExecutor> logger) : IResilienceExecutor
{
    private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new(StringComparer.Ordinal);
    private readonly IWireObserver[] _observers = observers.ToArray();

    public CircuitState GetCircuitState(string endpointKey)
        => _breakers.TryGetValue(endpointKey, out var b) ? b.State : CircuitState.Closed;

    public async Task<T> ExecuteAsync<T>(
        WireOperation op,
        Func<CancellationToken, Task<T>> action,
        Func<T, TransientFailure?>? classifyResult = null,
        Func<Exception, bool>? isTransient = null,
        CancellationToken cancellationToken = default)
    {
        var policy = op.Policy;
        var breaker = _breakers.GetOrAdd(op.EndpointKey, k => new CircuitBreaker(k, policy, time, OnCircuitChanged));
        using var activity = WireTelemetry.ActivitySource.StartActivity($"wire {op.Name}");
        activity?.SetTag("wire.endpoint", op.EndpointKey);
        var maxRetries = op.Retryable || policy.RetryNonIdempotent ? policy.MaxRetries : 0;

        for (var attempt = 1; ; attempt++)
        {
            if (!breaker.TryAcquire(out var retryAt))
            {
                WireTelemetry.CircuitRejections.Add(1, new KeyValuePair<string, object?>("endpoint", op.EndpointKey));
                throw new WireCircuitOpenException(op.EndpointKey, retryAt);
            }

            var started = Stopwatch.GetTimestamp();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(policy.AttemptTimeout);

            T? result = default;
            Exception? error = null;
            TransientFailure? failure = null;
            try
            {
                result = await action(attemptCts.Token).ConfigureAwait(false);
                failure = classifyResult?.Invoke(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                breaker.Release();
                throw;
            }
            catch (OperationCanceledException oce) when (attemptCts.IsCancellationRequested)
            {
                error = new WireTimeoutException(op.EndpointKey, policy.AttemptTimeout, oce);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            var transient = error is not null
                ? error is WireTimeoutException || (isTransient ?? DefaultIsTransient)(error)
                : failure is not null;
            var elapsed = Stopwatch.GetElapsedTime(started);

            if (!transient)
            {
                breaker.RecordSuccess(); // reachable endpoint; permanent errors are the caller's problem
                Notify(new WireAttempt(op.Name, op.EndpointKey, attempt, elapsed, error is null, false, error?.GetType().Name));
                if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
                return result!;
            }

            breaker.RecordFailure();
            var reason = failure?.Reason ?? error!.Message;
            var willRetry = attempt <= maxRetries && breaker.State != CircuitState.Open;
            Notify(new WireAttempt(op.Name, op.EndpointKey, attempt, elapsed, false, willRetry, reason));

            if (!willRetry)
            {
                if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
                return result!;
            }

            (result as IDisposable)?.Dispose();
            var delay = ComputeDelay(policy, attempt, failure?.RetryAfter);
            logger.LogWarning("Wire {Operation} attempt {Attempt} against {Endpoint} failed ({Reason}); retrying in {Delay}ms",
                op.Name, attempt, op.EndpointKey, reason, (int)delay.TotalMilliseconds);
            WireTelemetry.Retries.Add(1, new KeyValuePair<string, object?>("endpoint", op.EndpointKey));
            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static TimeSpan ComputeDelay(ResiliencePolicy p, int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } ra) return ra > p.MaxDelay ? p.MaxDelay : ra < TimeSpan.Zero ? TimeSpan.Zero : ra;
        var exp = Math.Min(p.MaxDelay.TotalMilliseconds, p.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(exp / 2 + Random.Shared.NextDouble() * exp / 2); // equal jitter
    }

    private static bool DefaultIsTransient(Exception ex) => ex is HttpRequestException or IOException;

    private void OnCircuitChanged(string key, CircuitState from, CircuitState to)
    {
        logger.LogWarning("Wire circuit for {Endpoint}: {From} -> {To}", key, from, to);
        foreach (var o in _observers)
            try { o.OnCircuitStateChanged(key, from, to); } catch { /* observers must not break calls */ }
    }

    private void Notify(WireAttempt a)
    {
        var tags = new KeyValuePair<string, object?>[] { new("endpoint", a.EndpointKey), new("operation", a.Operation), new("success", a.Success) };
        WireTelemetry.Attempts.Add(1, tags);
        WireTelemetry.Duration.Record(a.Duration.TotalMilliseconds, tags);
        foreach (var o in _observers)
            try { o.OnAttempt(a); } catch { /* observers must not break calls */ }
    }
}
