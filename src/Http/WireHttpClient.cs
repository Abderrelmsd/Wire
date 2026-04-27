using System.Net;
using Microsoft.Extensions.Options;
using Wire.Correlation;
using Wire.Resilience;

namespace Wire.Http;

internal sealed class WireHttpClient(HttpClient http, IResilienceExecutor executor, ICorrelationContext correlation, IOptions<WireOptions> options) : IWireHttpClient
{
    private static readonly HashSet<HttpMethod> IdempotentMethods = [HttpMethod.Get, HttpMethod.Head, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Options, HttpMethod.Trace];

    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, WireCallOptions? call = null, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var first = requestFactory();
        var uri = first.RequestUri ?? http.BaseAddress ?? throw new WireException("Request has no URI.");
        if (!uri.IsAbsoluteUri && http.BaseAddress is not null) uri = new Uri(http.BaseAddress, uri);
        var endpointKey = call?.EndpointKey ?? uri.GetLeftPart(UriPartial.Authority);
        var correlationId = call?.CorrelationId ?? correlation.CurrentOrNew();

        var idempotent = call?.Idempotent
            ?? (IdempotentMethods.Contains(first.Method) || first.Headers.Contains("Idempotency-Key"));
        var operation = new WireOperation($"http {first.Method}", endpointKey, opts.Resolve(call), idempotent);

        HttpRequestMessage? pending = first;
        return await executor.ExecuteAsync(
            operation,
            async ct =>
            {
                var request = pending ?? requestFactory();
                pending = null;
                try
                {
                    if (!request.Headers.Contains(opts.CorrelationHeader))
                        request.Headers.TryAddWithoutValidation(opts.CorrelationHeader, correlationId);
                    return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }
                finally { request.Dispose(); }
            },
            Classify,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static TransientFailure? Classify(HttpResponseMessage r)
    {
        if (r.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
            return null;

        TimeSpan? retryAfter = r.Headers.RetryAfter switch
        {
            { Delta: { } d } => d,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };
        return new TransientFailure($"HTTP {(int)r.StatusCode}", retryAfter);
    }
}
