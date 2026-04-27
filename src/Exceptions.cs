namespace Wire;

public class WireException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An attempt exceeded the policy's per-attempt timeout.</summary>
public sealed class WireTimeoutException(string endpointKey, TimeSpan timeout, Exception? inner = null)
    : WireException($"Call to '{endpointKey}' timed out after {timeout.TotalMilliseconds:0}ms.", inner);

/// <summary>The endpoint's circuit breaker is open; the call was not attempted.</summary>
public sealed class WireCircuitOpenException(string endpointKey, DateTimeOffset retryAt)
    : WireException($"Circuit for '{endpointKey}' is open until {retryAt:O}.")
{
    public string EndpointKey { get; } = endpointKey;
    public DateTimeOffset RetryAt { get; } = retryAt;
}

public sealed class GraphQlException(IReadOnlyList<Wire.GraphQl.GraphQlError> errors)
    : WireException("GraphQL request returned errors: " + string.Join("; ", errors.Select(e => e.Message)))
{
    public IReadOnlyList<Wire.GraphQl.GraphQlError> Errors { get; } = errors;
}
