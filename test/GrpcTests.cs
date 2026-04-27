using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wire.Correlation;
using Wire.Grpc;
using Wire.Resilience;

namespace Wire.Tests;

public class GrpcTests
{
    private static readonly Method<string, string> Echo = new(MethodType.Unary, "svc", "Echo",
        Marshallers.Create(s => System.Text.Encoding.UTF8.GetBytes(s), b => System.Text.Encoding.UTF8.GetString(b)),
        Marshallers.Create(s => System.Text.Encoding.UTF8.GetBytes(s), b => System.Text.Encoding.UTF8.GetString(b)));

    private sealed class FakeInvoker(Func<Metadata?, string> respond) : CallInvoker
    {
        public List<Metadata?> Seen { get; } = [];
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Seen.Add(options.Headers);
            try
            {
                var reply = (TResponse)(object)respond(options.Headers);
                return new AsyncUnaryCall<TResponse>(Task.FromResult(reply), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
            }
            catch (RpcException ex)
            {
                return new AsyncUnaryCall<TResponse>(Task.FromException<TResponse>(ex), Task.FromResult(new Metadata()), () => ex.Status, () => [], () => { });
            }
        }
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }

    private static (WireGrpcClient Client, FakeInvoker Invoker, ICorrelationContext Corr) Make(Func<Metadata?, string> respond, ResiliencePolicy? policy = null)
    {
        var invoker = new FakeInvoker(respond);
        var corr = new AsyncLocalCorrelationContext();
        var exec = new ResilienceExecutor([], TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ResilienceExecutor>.Instance);
        var opts = Options.Create(new WireOptions { Default = policy ?? Fast.Policy() });
        return (new WireGrpcClient(exec, corr, opts, _ => invoker), invoker, corr);
    }

    private static Task<string> CallEcho(CallInvoker inv, CancellationToken ct)
        => inv.AsyncUnaryCall(Echo, null, new CallOptions(cancellationToken: ct), "hi").ResponseAsync;

    [Fact]
    public async Task Unavailable_is_retried()
    {
        var n = 0;
        var (c, inv, _) = Make(_ => ++n < 3 ? throw new RpcException(new Status(StatusCode.Unavailable, "down")) : "pong");
        Assert.Equal("pong", await c.InvokeAsync("http://svc", CallEcho));
        Assert.Equal(3, inv.Seen.Count);
    }

    [Fact]
    public async Task Permanent_status_is_thrown_without_retry()
    {
        var (c, inv, _) = Make(_ => throw new RpcException(new Status(StatusCode.NotFound, "x")));
        var ex = await Assert.ThrowsAsync<RpcException>(() => c.InvokeAsync("http://svc", CallEcho));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
        Assert.Single(inv.Seen);
    }

    [Fact]
    public async Task Deadline_exceeded_retries_only_when_idempotent()
    {
        var (c, inv, _) = Make(_ => throw new RpcException(new Status(StatusCode.DeadlineExceeded, "slow")), Fast.Policy(retries: 2));
        await Assert.ThrowsAsync<RpcException>(() => c.InvokeAsync("http://svc", CallEcho, new WireCallOptions { Idempotent = false }));
        Assert.Single(inv.Seen);

        await Assert.ThrowsAsync<RpcException>(() => c.InvokeAsync("http://svc", CallEcho));
        Assert.Equal(1 + 3, inv.Seen.Count);
    }

    [Fact]
    public async Task Correlation_metadata_is_attached()
    {
        var (c, inv, corr) = Make(_ => "ok");
        using (corr.Begin("corr-9")) await c.InvokeAsync("http://svc", CallEcho);
        Assert.Equal("corr-9", inv.Seen.Single()!.GetValue(WireGrpcClient.CorrelationMetadataKey));
    }

    [Fact]
    public async Task Circuit_breaker_applies_to_grpc_addresses()
    {
        var policy = Fast.Policy(retries: 0, threshold: 2);
        var (c, inv, _) = Make(_ => throw new RpcException(new Status(StatusCode.Unavailable, "down")), policy);
        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<RpcException>(() => c.InvokeAsync("http://svc", CallEcho));
        await Assert.ThrowsAsync<WireCircuitOpenException>(() => c.InvokeAsync("http://svc", CallEcho));
        Assert.Equal(2, inv.Seen.Count);
    }

    [Fact]
    public void Adapters_resolve_from_di()
    {
        using var sp = new ServiceCollection().AddWire().BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IWireGrpcClient>());
        Assert.NotNull(sp.GetRequiredService<Wire.GraphQl.IWireGraphQlClient>());
        Assert.NotNull(sp.GetRequiredService<Wire.Http.IWireHttpClient>());
    }
}
