using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Wire.Correlation;
using Wire.GraphQl;
using Wire.Grpc;
using Wire.Http;
using Wire.Resilience;

namespace Wire;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the resilience executor, correlation context and the HTTP, GraphQL and gRPC adapters.</summary>
    /// <param name="configureHttpClient">Customize the underlying <see cref="HttpClient"/> (handlers, base address…). The per-attempt timeout is enforced by Wire, so HttpClient's own timeout is disabled.</param>
    public static IServiceCollection AddWire(this IServiceCollection services, Action<WireOptions>? configure = null, Action<IHttpClientBuilder>? configureHttpClient = null)
    {
        var builder = services.AddOptions<WireOptions>().ValidateOnStart();
        if (configure is not null) builder.Configure(configure);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WireOptions>, WireOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICorrelationContext, AsyncLocalCorrelationContext>();
        services.TryAddSingleton<IResilienceExecutor, ResilienceExecutor>();

        var http = services.AddHttpClient<IWireHttpClient, WireHttpClient>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        configureHttpClient?.Invoke(http);

        services.TryAddTransient<IWireGraphQlClient, WireGraphQlClient>();
        services.TryAddSingleton<IWireGrpcClient>(sp => new WireGrpcClient(
            sp.GetRequiredService<IResilienceExecutor>(), sp.GetRequiredService<ICorrelationContext>(), sp.GetRequiredService<IOptions<WireOptions>>()));
        return services;
    }

    /// <summary>
    /// Registers a second <see cref="IWireHttpClient"/> under <paramref name="key"/>, with its own underlying <see cref="HttpClient"/>
    /// (handlers, base address…) but the same policies, circuits, correlation and telemetry. Use it when calls need different transport
    /// rules, for example a client restricted to public addresses for user-supplied URLs beside one for the internal network. Resolve it with
    /// <c>[FromKeyedServices(key)] IWireHttpClient</c>. Call <see cref="AddWire"/> as well.
    /// </summary>
    public static IServiceCollection AddKeyedWireHttpClient(this IServiceCollection services, string key, Action<IHttpClientBuilder>? configureHttpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var name = "Wire:" + key;
        var http = services.AddHttpClient(name, c => c.Timeout = Timeout.InfiniteTimeSpan);
        configureHttpClient?.Invoke(http);
        services.AddKeyedTransient<IWireHttpClient>(key, (sp, _) => new WireHttpClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(name), sp.GetRequiredService<IResilienceExecutor>(),
            sp.GetRequiredService<ICorrelationContext>(), sp.GetRequiredService<IOptions<WireOptions>>()));
        return services;
    }

    /// <summary>Registers a telemetry observer.</summary>
    public static IServiceCollection AddWireObserver<T>(this IServiceCollection services) where T : class, IWireObserver
        => services.AddSingleton<IWireObserver, T>();
}
