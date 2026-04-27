using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Wire.Correlation;
using Wire.Http;
using Wire.Resilience;

namespace Wire.Tests;

public class HttpTests
{
    private static readonly Uri Url = new("https://api.example.test/items");

    [Fact]
    public async Task Retries_transient_status_then_succeeds()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.BadGateway).Then(HttpStatusCode.OK);
        using var sp = Fast.Build(h);
        using var r = await sp.GetRequiredService<IWireHttpClient>().GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(3, h.Calls);
    }

    [Fact]
    public async Task Does_not_retry_client_errors()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.NotFound);
        using var sp = Fast.Build(h);
        using var r = await sp.GetRequiredService<IWireHttpClient>().GetAsync(Url);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Exhausted_retries_return_last_response()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.InternalServerError);
        using var sp = Fast.Build(h, Fast.Policy(retries: 2));
        using var r = await sp.GetRequiredService<IWireHttpClient>().GetAsync(Url);
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Equal(3, h.Calls);
    }

    [Fact]
    public async Task Retries_on_network_exceptions_and_rethrows_when_exhausted()
    {
        var h = new ScriptedHandler().Throw(new HttpRequestException("boom")).Then(HttpStatusCode.OK);
        using var sp = Fast.Build(h);
        using var ok = await sp.GetRequiredService<IWireHttpClient>().GetAsync(Url);
        Assert.Equal(2, h.Calls);

        var h2 = new ScriptedHandler().Throw(new HttpRequestException("a")).Throw(new HttpRequestException("b"));
        using var sp2 = Fast.Build(h2, Fast.Policy(retries: 1));
        await Assert.ThrowsAsync<HttpRequestException>(() => sp2.GetRequiredService<IWireHttpClient>().GetAsync(Url));
        Assert.Equal(2, h2.Calls);
    }

    [Fact]
    public async Task Post_is_not_retried_by_default()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable);
        using var sp = Fast.Build(h);
        using var r = await sp.GetRequiredService<IWireHttpClient>().SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Url));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Post_with_idempotency_key_or_flag_is_retried()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.OK);
        using var sp = Fast.Build(h);
        using var r = await sp.GetRequiredService<IWireHttpClient>().SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, Url);
            m.Headers.Add("Idempotency-Key", "abc");
            return m;
        });
        Assert.Equal(2, h.Calls);

        var h2 = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.OK);
        using var sp2 = Fast.Build(h2);
        using var r2 = await sp2.GetRequiredService<IWireHttpClient>().SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Url), new WireCallOptions { Idempotent = true });
        Assert.Equal(2, h2.Calls);
    }

    [Fact]
    public async Task Retry_after_header_is_honoured_up_to_max_delay()
    {
        var h = new ScriptedHandler()
            .Then(HttpStatusCode.TooManyRequests, tweak: r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120)))
            .Then(HttpStatusCode.OK);
        var policy = Fast.Policy();
        policy.MaxDelay = TimeSpan.FromMilliseconds(60);
        using var sp = Fast.Build(h, policy);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var r = await sp.GetRequiredService<IWireHttpClient>().GetAsync(Url);
        Assert.InRange(sw.ElapsedMilliseconds, 55, 1500); // capped at MaxDelay, not 120s
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Correlation_id_is_stamped_on_every_attempt()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.OK);
        using var sp = Fast.Build(h);
        using (sp.GetRequiredService<ICorrelationContext>().Begin("corr-123"))
            (await sp.GetRequiredService<IWireHttpClient>().GetAsync(Url)).Dispose();

        Assert.All(h.Requests, r => Assert.Equal("corr-123", r.Headers.GetValues("X-Correlation-Id").Single()));
    }

    [Fact]
    public async Task Correlation_id_is_generated_when_absent_and_stable_across_retries()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.OK);
        using var sp = Fast.Build(h);
        (await sp.GetRequiredService<IWireHttpClient>().GetAsync(Url)).Dispose();
        var ids = h.Requests.Select(r => r.Headers.GetValues("X-Correlation-Id").Single()).Distinct().ToList();
        Assert.Single(ids);
        Assert.NotEmpty(ids[0]);
    }

    [Fact]
    public async Task Attempt_timeout_raises_WireTimeoutException()
    {
        var h = new ScriptedHandler().Then(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        var policy = Fast.Policy(retries: 0);
        policy.AttemptTimeout = TimeSpan.FromMilliseconds(50);
        using var sp = Fast.Build(h, policy);
        await Assert.ThrowsAsync<WireTimeoutException>(() => sp.GetRequiredService<IWireHttpClient>().GetAsync(Url));
    }

    [Fact]
    public async Task Timeouts_are_retried()
    {
        var h = new ScriptedHandler()
            .Then(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); })
            .Then(HttpStatusCode.OK);
        var policy = Fast.Policy(retries: 1);
        policy.AttemptTimeout = TimeSpan.FromMilliseconds(50);
        using var sp = Fast.Build(h, policy);
        (await sp.GetRequiredService<IWireHttpClient>().GetAsync(Url)).Dispose();
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried_or_wrapped()
    {
        var h = new ScriptedHandler().Then(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        using var sp = Fast.Build(h);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sp.GetRequiredService<IWireHttpClient>().GetAsync(Url, ct: cts.Token));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Json_helpers_roundtrip_and_throw_on_error_status()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.OK, """{"name":"x"}""").Then(HttpStatusCode.NotFound);
        using var sp = Fast.Build(h);
        var c = sp.GetRequiredService<IWireHttpClient>();
        var item = await c.GetJsonAsync<Item>(Url);
        Assert.Equal("x", item!.Name);
        await Assert.ThrowsAsync<HttpRequestException>(() => c.GetJsonAsync<Item>(Url));
    }

    private sealed record Item(string Name);

    [Fact]
    public async Task Named_policy_can_be_selected_per_call()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable);
        var services = new ServiceCollection();
        services.AddWire(o => { o.Default = Fast.Policy(3); o.Policies["none"] = Fast.Policy(0); }, http => http.ConfigurePrimaryHttpMessageHandler(() => h));
        using var sp = services.BuildServiceProvider();
        (await sp.GetRequiredService<IWireHttpClient>().GetAsync(Url, new WireCallOptions { PolicyName = "none" })).Dispose();
        Assert.Equal(1, h.Calls);
        await Assert.ThrowsAsync<WireException>(() => sp.GetRequiredService<IWireHttpClient>().GetAsync(Url, new WireCallOptions { PolicyName = "missing" }));
    }
}
