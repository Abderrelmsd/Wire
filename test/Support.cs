using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Wire.Resilience;

namespace Wire.Tests;

/// <summary>Scripted handler: each call pops the next step (response factory or exception).</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _steps = new();
    public List<HttpRequestMessage> Requests { get; } = [];
    public int Calls => Requests.Count;

    public ScriptedHandler Then(HttpStatusCode code, string body = "{}", Action<HttpResponseMessage>? tweak = null)
        => Then((_, _) =>
        {
            var r = new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            tweak?.Invoke(r);
            return Task.FromResult(r);
        });

    public ScriptedHandler Throw(Exception ex) => Then((_, _) => throw ex);

    public ScriptedHandler Then(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step) { _steps.Enqueue(step); return this; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Snapshot headers: the request is disposed by the client after each attempt.
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var h in request.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        Requests.Add(copy);
        return _steps.Count > 0 ? _steps.Dequeue()(request, ct) : throw new InvalidOperationException("No scripted step left.");
    }
}

internal sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class RecordingObserver : IWireObserver
{
    public List<WireAttempt> Attempts { get; } = [];
    public List<(CircuitState From, CircuitState To)> Transitions { get; } = [];
    public void OnAttempt(WireAttempt a) => Attempts.Add(a);
    public void OnCircuitStateChanged(string key, CircuitState from, CircuitState to) => Transitions.Add((from, to));
}

internal static class Fast
{
    public static ResiliencePolicy Policy(int retries = 3, int threshold = 0) => new()
    {
        MaxRetries = retries, BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5),
        AttemptTimeout = TimeSpan.FromSeconds(5), CircuitFailureThreshold = threshold, CircuitBreakDuration = TimeSpan.FromSeconds(30),
    };

    public static ServiceProvider Build(ScriptedHandler handler, ResiliencePolicy? policy = null, TimeProvider? time = null, RecordingObserver? observer = null)
    {
        var services = new ServiceCollection();
        if (time is not null) services.AddSingleton(time);
        if (observer is not null) services.AddSingleton<IWireObserver>(observer);
        services.AddWire(o => o.Default = policy ?? Policy(), http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        return services.BuildServiceProvider();
    }
}
