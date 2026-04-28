# Wire

One resilient way to call other systems over **HTTP**, **GraphQL** and **gRPC**. All three share the same retry, timeout, circuit-breaker, correlation and telemetry behaviour, so a policy you write once applies to every protocol.

Use it for any outbound call where a transient failure should not become your outage: third-party APIs, internal services, connectors.

## Install

```bash
dotnet add package Wire
```

## Quick start

```csharp
services.AddWire(o =>
{
    o.Default = new ResiliencePolicy { MaxRetries = 3, AttemptTimeout = TimeSpan.FromSeconds(10), CircuitFailureThreshold = 5 };
    o.Policies["payments"] = new ResiliencePolicy { MaxRetries = 0 };      // named policy, chosen per call
});

// HTTP
using var res = await http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), new WireCallOptions { PolicyName = "payments" });
var dto = await http.GetJsonAsync<Dto>(uri);

// GraphQL: queries are retried, mutations are not
var r = await gql.ExecuteAsync<Data>(endpoint, new GraphQlRequest("query { viewer { login } }"));

// gRPC
var reply = await grpc.InvokeAsync("https://svc:443",
    (invoker, ct) => new Greeter.GreeterClient(invoker).SayHelloAsync(req, cancellationToken: ct).ResponseAsync);
```

HTTP requests are passed as a factory (`() => new HttpRequestMessage(...)`) because an `HttpRequestMessage` cannot be sent twice, and a retry needs a fresh one.

## What it does for you

**Retries with backoff.** Exponential backoff with jitter, capped at `MaxDelay`. A `Retry-After` header is honoured, also capped at `MaxDelay`.

**Retries only when it is safe.**
- HTTP: idempotent methods, calls marked `Idempotent`, requests carrying an `Idempotency-Key`, or policies with `RetryNonIdempotent`.
- GraphQL: queries yes, mutations no.
- gRPC: `Unavailable` always; `DeadlineExceeded`, `ResourceExhausted`, `Aborted` and `Internal` only when the call is idempotent (the default for unary calls).

**Per-attempt timeouts.** Enforced by Wire itself, and a timeout surfaces as `WireTimeoutException` and is retryable. Cancellation by the caller is never retried or wrapped.

**Circuit breaker per endpoint.** After `CircuitFailureThreshold` consecutive transient failures the circuit opens for `CircuitBreakDuration`, then lets a single probe through (half-open). The endpoint key is scheme, host and port for HTTP and the address for gRPC; override it with `WireCallOptions.EndpointKey`. Permanent errors such as HTTP 4xx count as a healthy endpoint.

**Correlation ids.** `ICorrelationContext.Begin(id)` flows an id through async calls. It is sent as `X-Correlation-Id` (HTTP) or `x-correlation-id` (gRPC), generated if absent, and is the same across retries.

**Telemetry.** Register observers with `services.AddWireObserver<T>()`. Wire also emits an `ActivitySource` and `Meter` named `Wire`.

## Behaviour to know about

- When retries are exhausted on a bad HTTP status, the **last response is returned** rather than an exception being thrown, since callers already handle status codes. Exceptions are rethrown as they were.
- GraphQL errors are **data**, not transport failures: read `GraphQlResponse.Errors`, or call `EnsureNoErrors()` to throw a `GraphQlException`.
- gRPC channels are cached per address. For streaming calls only the lambda that starts the call is retried.

## Configuration

Section `Wire`. Each `ResiliencePolicy` has:

| Option | Default | Meaning |
|---|---|---|
| `MaxRetries` | `3` | Retries after the first attempt (0 disables retrying) |
| `BaseDelay` | `200ms` | First backoff delay |
| `MaxDelay` | `10s` | Cap for backoff and `Retry-After` |
| `AttemptTimeout` | `30s` | Timeout of each attempt |
| `CircuitFailureThreshold` | `5` | Consecutive failures that open the circuit (0 disables the breaker) |
| `CircuitBreakDuration` | `30s` | How long the circuit stays open |
| `RetryNonIdempotent` | `false` | Also retry POST/PATCH; only enable with idempotency keys |

Top-level: `Default` (the policy used unless a call names another), `Policies` (named policies) and `CorrelationHeader` (default `X-Correlation-Id`).

Per-call overrides go in `WireCallOptions`: `PolicyName`, `Policy`, `CorrelationId`, `Idempotent` and `EndpointKey`.

## Custom protocols

Use `IResilienceExecutor` directly. It is what the HTTP, GraphQL and gRPC adapters use, and Bridge, Tunnel and Warden build on it.

## Design notes

Wire has its own small resilience core instead of depending on Polly, which keeps the dependency graph small and makes retry, breaker and telemetry behaviour identical across protocols. Time is read from `TimeProvider`, so all of it is testable.

## Depends on

Nothing else from this set of packages.
