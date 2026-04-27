using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Wire.Resilience;

public enum CircuitState { Closed, Open, HalfOpen }

public sealed record WireAttempt(string Operation, string EndpointKey, int Attempt, TimeSpan Duration, bool Success, bool WillRetry, string? Failure);

/// <summary>Telemetry hook. Register any number; exceptions thrown by observers are swallowed.</summary>
public interface IWireObserver
{
    void OnAttempt(WireAttempt attempt) { }
    void OnCircuitStateChanged(string endpointKey, CircuitState from, CircuitState to) { }
}

public static class WireTelemetry
{
    public const string Name = "Wire";
    public static readonly ActivitySource ActivitySource = new(Name);
    internal static readonly Meter Meter = new(Name);
    internal static readonly Counter<long> Attempts = Meter.CreateCounter<long>("wire.attempts");
    internal static readonly Counter<long> Retries = Meter.CreateCounter<long>("wire.retries");
    internal static readonly Counter<long> CircuitRejections = Meter.CreateCounter<long>("wire.circuit_rejections");
    internal static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("wire.attempt.duration", "ms");
}
