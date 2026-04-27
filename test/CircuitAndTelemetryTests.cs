using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wire.Http;
using Wire.Resilience;

namespace Wire.Tests;

public class CircuitAndTelemetryTests
{
    private static readonly Uri Url = new("https://flaky.example.test/x");
    private static readonly Uri Other = new("https://healthy.example.test/x");

    [Fact]
    public async Task Circuit_opens_after_threshold_and_rejects_without_calling()
    {
        var h = new ScriptedHandler();
        for (var i = 0; i < 3; i++) h.Then(HttpStatusCode.ServiceUnavailable);
        var time = new FakeTime(DateTimeOffset.UtcNow);
        var obs = new RecordingObserver();
        using var sp = Fast.Build(h, Fast.Policy(retries: 0, threshold: 3), time, obs);
        var c = sp.GetRequiredService<IWireHttpClient>();

        for (var i = 0; i < 3; i++) (await c.GetAsync(Url)).Dispose();
        var ex = await Assert.ThrowsAsync<WireCircuitOpenException>(() => c.GetAsync(Url));

        Assert.Equal(3, h.Calls);
        Assert.Equal(CircuitState.Open, sp.GetRequiredService<IResilienceExecutor>().GetCircuitState("https://flaky.example.test"));
        Assert.Equal("https://flaky.example.test", ex.EndpointKey);
        Assert.Contains((CircuitState.Closed, CircuitState.Open), obs.Transitions);
    }

    [Fact]
    public async Task Circuits_are_isolated_per_endpoint()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.OK);
        using var sp = Fast.Build(h, Fast.Policy(retries: 0, threshold: 1));
        var c = sp.GetRequiredService<IWireHttpClient>();
        (await c.GetAsync(Url)).Dispose();
        await Assert.ThrowsAsync<WireCircuitOpenException>(() => c.GetAsync(Url));
        (await c.GetAsync(Other)).Dispose(); // unaffected
    }

    [Fact]
    public async Task Half_open_probe_success_closes_and_failure_reopens()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.OK);
        var time = new FakeTime(DateTimeOffset.UtcNow);
        var obs = new RecordingObserver();
        using var sp = Fast.Build(h, Fast.Policy(retries: 0, threshold: 1), time, obs);
        var c = sp.GetRequiredService<IWireHttpClient>();
        var exec = sp.GetRequiredService<IResilienceExecutor>();
        const string key = "https://flaky.example.test";

        (await c.GetAsync(Url)).Dispose();                  // opens
        await Assert.ThrowsAsync<WireCircuitOpenException>(() => c.GetAsync(Url));

        time.Now += TimeSpan.FromSeconds(31);
        (await c.GetAsync(Url)).Dispose();                  // half-open probe fails -> reopen
        Assert.Equal(CircuitState.Open, exec.GetCircuitState(key));
        await Assert.ThrowsAsync<WireCircuitOpenException>(() => c.GetAsync(Url));

        time.Now += TimeSpan.FromSeconds(31);
        (await c.GetAsync(Url)).Dispose();                  // probe succeeds -> closed
        Assert.Equal(CircuitState.Closed, exec.GetCircuitState(key));
        Assert.Contains((CircuitState.HalfOpen, CircuitState.Closed), obs.Transitions);
    }

    [Fact]
    public async Task Circuit_opening_stops_further_retries()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.ServiceUnavailable);
        using var sp = Fast.Build(h, Fast.Policy(retries: 5, threshold: 2));
        (await sp.GetRequiredService<IWireHttpClient>().GetAsync(Url)).Dispose();
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Permanent_errors_do_not_trip_the_breaker()
    {
        var h = new ScriptedHandler();
        for (var i = 0; i < 5; i++) h.Then(HttpStatusCode.BadRequest);
        using var sp = Fast.Build(h, Fast.Policy(retries: 0, threshold: 2));
        var c = sp.GetRequiredService<IWireHttpClient>();
        for (var i = 0; i < 5; i++) (await c.GetAsync(Url)).Dispose();
        Assert.Equal(5, h.Calls);
    }

    [Fact]
    public async Task Observers_see_every_attempt_and_a_throwing_observer_is_harmless()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.OK);
        var obs = new RecordingObserver();
        var services = new ServiceCollection();
        services.AddSingleton<IWireObserver>(new Throwing());
        services.AddSingleton<IWireObserver>(obs);
        services.AddWire(o => o.Default = Fast.Policy(), http => http.ConfigurePrimaryHttpMessageHandler(() => h));
        using var sp = services.BuildServiceProvider();

        (await sp.GetRequiredService<IWireHttpClient>().GetAsync(Url)).Dispose();

        Assert.Equal(2, obs.Attempts.Count);
        Assert.False(obs.Attempts[0].Success);
        Assert.True(obs.Attempts[0].WillRetry);
        Assert.True(obs.Attempts[1].Success);
    }

    private sealed class Throwing : IWireObserver
    {
        public void OnAttempt(WireAttempt attempt) => throw new InvalidOperationException("observer bug");
    }

    [Fact]
    public void Invalid_options_fail_validation()
    {
        var services = new ServiceCollection();
        services.AddWire(o => o.Default.AttemptTimeout = TimeSpan.Zero);
        using var sp = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<WireOptions>>().Value);
    }
}
