using System.Net.Http.Json;

namespace Wire.Http;

/// <summary>HTTP transport with the shared Wire resilience surface.</summary>
public interface IWireHttpClient
{
    /// <summary>
    /// Sends a request with retry/backoff, timeout, circuit breaking and correlation-id stamping.
    /// <paramref name="requestFactory"/> is invoked once per attempt (requests cannot be re-sent).
    /// Retries run on: 408/429/500/502/503/504 (honouring Retry-After), <see cref="HttpRequestException"/> and timeouts.
    /// Non-idempotent methods (POST/PATCH) are retried only when the call is marked idempotent,
    /// carries an <c>Idempotency-Key</c> header, or the policy sets <c>RetryNonIdempotent</c>.
    /// If retries are exhausted on a bad status, that last response is returned (not thrown).
    /// </summary>
    Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, WireCallOptions? options = null, CancellationToken cancellationToken = default);
}

public static class WireHttpClientExtensions
{
    public static Task<HttpResponseMessage> GetAsync(this IWireHttpClient client, Uri uri, WireCallOptions? options = null, CancellationToken ct = default)
        => client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), options, ct);

    /// <summary>GET and deserialize JSON; throws <see cref="HttpRequestException"/> on a non-success status.</summary>
    public static async Task<T?> GetJsonAsync<T>(this IWireHttpClient client, Uri uri, WireCallOptions? options = null, CancellationToken ct = default)
    {
        using var response = await client.GetAsync(uri, options, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
    }

    /// <summary>POST JSON and deserialize the JSON reply; throws on a non-success status.</summary>
    public static async Task<TResponse?> PostJsonAsync<TRequest, TResponse>(this IWireHttpClient client, Uri uri, TRequest body, WireCallOptions? options = null, CancellationToken ct = default)
    {
        using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) }, options, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(ct).ConfigureAwait(false);
    }
}
