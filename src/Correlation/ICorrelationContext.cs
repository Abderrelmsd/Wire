namespace Wire.Correlation;

/// <summary>Ambient correlation id, flowed across async calls and stamped onto every outbound request.</summary>
public interface ICorrelationContext
{
    /// <summary>The current id, or null when none has been started.</summary>
    string? Current { get; }

    /// <summary>Makes <paramref name="correlationId"/> current until the returned scope is disposed.</summary>
    IDisposable Begin(string correlationId);

    /// <summary>Current id, or a freshly generated one when none is active.</summary>
    string CurrentOrNew();
}

internal sealed class AsyncLocalCorrelationContext : ICorrelationContext
{
    private static readonly AsyncLocal<string?> Slot = new();

    public string? Current => Slot.Value;
    public string CurrentOrNew() => Slot.Value ?? Guid.NewGuid().ToString("N");

    public IDisposable Begin(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var previous = Slot.Value;
        Slot.Value = correlationId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
    }
}
