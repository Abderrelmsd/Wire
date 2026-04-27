using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Wire.Http;

namespace Wire.GraphQl;

public sealed record GraphQlRequest(string Query, object? Variables = null, string? OperationName = null);

public sealed record GraphQlError(string Message, JsonElement? Path = null, JsonElement? Extensions = null);

public sealed record GraphQlResponse<T>(T? Data, IReadOnlyList<GraphQlError> Errors)
{
    public bool HasErrors => Errors.Count > 0;

    /// <summary>Returns <see cref="Data"/>, or throws <see cref="GraphQlException"/> if the server reported errors.</summary>
    public T? EnsureNoErrors() => HasErrors ? throw new GraphQlException(Errors) : Data;
}

/// <summary>GraphQL over HTTP, on the shared Wire resilience surface.</summary>
public interface IWireGraphQlClient
{
    /// <summary>
    /// Executes a query/mutation. Queries are treated as idempotent (retried); mutations are not,
    /// unless <see cref="WireCallOptions.Idempotent"/> says otherwise. GraphQL-level errors are returned, never retried.
    /// </summary>
    Task<GraphQlResponse<T>> ExecuteAsync<T>(Uri endpoint, GraphQlRequest request, IReadOnlyDictionary<string, string>? headers = null, WireCallOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed partial class WireGraphQlClient(IWireHttpClient http) : IWireGraphQlClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<GraphQlResponse<T>> ExecuteAsync<T>(Uri endpoint, GraphQlRequest request, IReadOnlyDictionary<string, string>? headers = null, WireCallOptions? options = null, CancellationToken cancellationToken = default)
    {
        var idempotent = options?.Idempotent ?? !IsMutation(request.Query);
        var call = new WireCallOptions
        {
            Idempotent = idempotent, Policy = options?.Policy, PolicyName = options?.PolicyName,
            CorrelationId = options?.CorrelationId, EndpointKey = options?.EndpointKey,
        };

        using var response = await http.SendAsync(() =>
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new { query = request.Query, variables = request.Variables, operationName = request.OperationName }, options: Json),
            };
            msg.Headers.Accept.ParseAdd("application/json");
            if (headers is not null) foreach (var (k, v) in headers) msg.Headers.TryAddWithoutValidation(k, v);
            return msg;
        }, call, cancellationToken).ConfigureAwait(false);

        // GraphQL servers commonly return 200 or 400 with an {errors:[...]} body; only fail when there is no such body.
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Envelope<T>? envelope = null;
        try { envelope = JsonSerializer.Deserialize<Envelope<T>>(raw, Json); } catch (JsonException) { }
        if (envelope is null || (envelope.Data is null && envelope.Errors is null))
        {
            response.EnsureSuccessStatusCode();
            throw new WireException("GraphQL response was not a valid GraphQL payload.");
        }
        return new GraphQlResponse<T>(envelope.Data, envelope.Errors ?? []);
    }

    /// <summary>True when the first operation in the document is a mutation (comments and fragments ignored).</summary>
    internal static bool IsMutation(string query)
    {
        var stripped = CommentRegex().Replace(query, "").TrimStart();
        return stripped.StartsWith("mutation", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"#[^\r\n]*")]
    private static partial Regex CommentRegex();

    private sealed record Envelope<T>(T? Data, List<GraphQlError>? Errors);
}
