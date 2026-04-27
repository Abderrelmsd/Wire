using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using Wire.Correlation;
using Wire.Resilience;

namespace Wire.Grpc;

/// <summary>gRPC transport with the shared Wire resilience surface.</summary>
public interface IWireGrpcClient
{
    /// <summary>
    /// Runs a unary (or otherwise re-invokable) call against <paramref name="address"/> with retry, timeout, circuit breaking and correlation metadata.
    /// <c>Unavailable</c> is always retried; <c>DeadlineExceeded</c>, <c>ResourceExhausted</c>, <c>Aborted</c> and <c>Internal</c> only when the call is idempotent (default: true).
    /// </summary>
    Task<TResponse> InvokeAsync<TResponse>(string address, Func<CallInvoker, CancellationToken, Task<TResponse>> call, WireCallOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class WireGrpcClient(IResilienceExecutor executor, ICorrelationContext correlation, IOptions<WireOptions> options, Func<string, CallInvoker>? invokerFactory = null)
    : IWireGrpcClient, IDisposable
{
    public const string CorrelationMetadataKey = "x-correlation-id";
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new(StringComparer.Ordinal);
    private readonly Func<string, CallInvoker> _invokerFactory = invokerFactory ?? (_ => throw new InvalidOperationException());
    private readonly bool _useChannels = invokerFactory is null;

    public async Task<TResponse> InvokeAsync<TResponse>(string address, Func<CallInvoker, CancellationToken, Task<TResponse>> call, WireCallOptions? callOptions = null, CancellationToken cancellationToken = default)
    {
        var idempotent = callOptions?.Idempotent ?? true;
        var operation = new WireOperation("grpc", callOptions?.EndpointKey ?? address, options.Value.Resolve(callOptions), idempotent);
        var invoker = (_useChannels ? _channels.GetOrAdd(address, GrpcChannel.ForAddress).CreateCallInvoker() : _invokerFactory(address))
            .Intercept(new CorrelationInterceptor(callOptions?.CorrelationId ?? correlation.CurrentOrNew()));

        return await executor.ExecuteAsync(operation, ct => call(invoker, ct), isTransient: ex => IsTransient(ex, idempotent), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsTransient(Exception ex, bool idempotent) => ex is RpcException rpc && rpc.StatusCode switch
    {
        StatusCode.Unavailable => true,
        StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted or StatusCode.Aborted or StatusCode.Internal => idempotent,
        _ => false,
    };

    public void Dispose()
    {
        foreach (var c in _channels.Values) c.Dispose();
    }

    private sealed class CorrelationInterceptor(string id) : Interceptor
    {
        private ClientInterceptorContext<TReq, TResp> With<TReq, TResp>(ClientInterceptorContext<TReq, TResp> c)
            where TReq : class where TResp : class
        {
            var headers = c.Options.Headers ?? [];
            if (headers.All(h => h.Key != CorrelationMetadataKey)) headers.Add(CorrelationMetadataKey, id);
            return new ClientInterceptorContext<TReq, TResp>(c.Method, c.Host, c.Options.WithHeaders(headers));
        }

        public override AsyncUnaryCall<TResp> AsyncUnaryCall<TReq, TResp>(TReq request, ClientInterceptorContext<TReq, TResp> context, AsyncUnaryCallContinuation<TReq, TResp> continuation)
            => continuation(request, With(context));

        public override TResp BlockingUnaryCall<TReq, TResp>(TReq request, ClientInterceptorContext<TReq, TResp> context, BlockingUnaryCallContinuation<TReq, TResp> continuation)
            => continuation(request, With(context));

        public override AsyncServerStreamingCall<TResp> AsyncServerStreamingCall<TReq, TResp>(TReq request, ClientInterceptorContext<TReq, TResp> context, AsyncServerStreamingCallContinuation<TReq, TResp> continuation)
            => continuation(request, With(context));

        public override AsyncClientStreamingCall<TReq, TResp> AsyncClientStreamingCall<TReq, TResp>(ClientInterceptorContext<TReq, TResp> context, AsyncClientStreamingCallContinuation<TReq, TResp> continuation)
            => continuation(With(context));

        public override AsyncDuplexStreamingCall<TReq, TResp> AsyncDuplexStreamingCall<TReq, TResp>(ClientInterceptorContext<TReq, TResp> context, AsyncDuplexStreamingCallContinuation<TReq, TResp> continuation)
            => continuation(With(context));
    }
}
