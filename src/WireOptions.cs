namespace Wire;

/// <summary>Resilience settings shared by every protocol adapter.</summary>
public sealed class ResiliencePolicy
{
    /// <summary>Retries after the first attempt (0 disables retrying).</summary>
    public int MaxRetries { get; set; } = 3;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Per-attempt timeout.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Consecutive transient failures that open the circuit (0 disables the breaker).</summary>
    public int CircuitFailureThreshold { get; set; } = 5;
    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Retry non-idempotent calls (HTTP POST/PATCH) too. Off by default; enable only with idempotency keys.</summary>
    public bool RetryNonIdempotent { get; set; }

    public ResiliencePolicy Clone() => (ResiliencePolicy)MemberwiseClone();
}

public sealed class WireOptions
{
    public const string SectionName = "Wire";

    public ResiliencePolicy Default { get; set; } = new();

    /// <summary>Named policies selectable per call via <see cref="WireCallOptions.PolicyName"/>.</summary>
    public Dictionary<string, ResiliencePolicy> Policies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string CorrelationHeader { get; set; } = "X-Correlation-Id";
}

/// <summary>Per-call overrides accepted by every adapter.</summary>
public sealed class WireCallOptions
{
    public string? PolicyName { get; init; }
    public ResiliencePolicy? Policy { get; init; }
    public string? CorrelationId { get; init; }
    /// <summary>Force (true) or forbid (false) retrying. Null = protocol default.</summary>
    public bool? Idempotent { get; init; }
    /// <summary>Overrides the circuit-breaker key (default: scheme+host+port / gRPC address).</summary>
    public string? EndpointKey { get; init; }
}

internal sealed class WireOptionsValidator : Microsoft.Extensions.Options.IValidateOptions<WireOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, WireOptions o)
    {
        var errors = new List<string>();
        void Check(string n, ResiliencePolicy p)
        {
            if (p.MaxRetries < 0) errors.Add($"{n}: MaxRetries must be >= 0.");
            if (p.BaseDelay < TimeSpan.Zero || p.MaxDelay < p.BaseDelay) errors.Add($"{n}: delays invalid (0 <= BaseDelay <= MaxDelay).");
            if (p.AttemptTimeout <= TimeSpan.Zero) errors.Add($"{n}: AttemptTimeout must be > 0.");
            if (p.CircuitFailureThreshold < 0) errors.Add($"{n}: CircuitFailureThreshold must be >= 0.");
            if (p.CircuitFailureThreshold > 0 && p.CircuitBreakDuration <= TimeSpan.Zero) errors.Add($"{n}: CircuitBreakDuration must be > 0.");
        }
        Check("Default", o.Default);
        foreach (var (k, v) in o.Policies) Check($"Policies[{k}]", v);
        if (string.IsNullOrWhiteSpace(o.CorrelationHeader)) errors.Add("CorrelationHeader is required.");
        return errors.Count == 0 ? Microsoft.Extensions.Options.ValidateOptionsResult.Success : Microsoft.Extensions.Options.ValidateOptionsResult.Fail(errors);
    }
}
