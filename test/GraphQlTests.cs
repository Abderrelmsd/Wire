using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Wire.GraphQl;

namespace Wire.Tests;

public class GraphQlTests
{
    private static readonly Uri Endpoint = new("https://gql.example.test/graphql");
    private sealed record Viewer(string Login);
    private sealed record Data(Viewer Viewer);

    [Fact]
    public async Task Query_returns_typed_data()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.OK, """{"data":{"viewer":{"login":"octo"}}}""");
        using var sp = Fast.Build(h);
        var r = await sp.GetRequiredService<IWireGraphQlClient>().ExecuteAsync<Data>(Endpoint, new GraphQlRequest("query { viewer { login } }"));
        Assert.Equal("octo", r.EnsureNoErrors()!.Viewer.Login);
    }

    [Fact]
    public async Task Queries_are_retried_but_mutations_are_not()
    {
        var q = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.OK, """{"data":{"viewer":{"login":"a"}}}""");
        using var sp = Fast.Build(q);
        await sp.GetRequiredService<IWireGraphQlClient>().ExecuteAsync<Data>(Endpoint, new GraphQlRequest("# hi\nquery Q { viewer { login } }"));
        Assert.Equal(2, q.Calls);

        var m = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable);
        using var sp2 = Fast.Build(m);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            sp2.GetRequiredService<IWireGraphQlClient>().ExecuteAsync<Data>(Endpoint, new GraphQlRequest("mutation { like }")));
        Assert.Equal(1, m.Calls);
    }

    [Fact]
    public async Task Graphql_errors_are_surfaced_and_not_retried()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.OK, """{"data":null,"errors":[{"message":"nope"}]}""");
        using var sp = Fast.Build(h);
        var r = await sp.GetRequiredService<IWireGraphQlClient>().ExecuteAsync<Data>(Endpoint, new GraphQlRequest("query { x }"));
        Assert.True(r.HasErrors);
        Assert.Equal("nope", r.Errors[0].Message);
        Assert.Throws<GraphQlException>(() => r.EnsureNoErrors());
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Errors_body_on_400_is_still_parsed()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.BadRequest, """{"errors":[{"message":"syntax"}]}""");
        using var sp = Fast.Build(h);
        var r = await sp.GetRequiredService<IWireGraphQlClient>().ExecuteAsync<Data>(Endpoint, new GraphQlRequest("query {"));
        Assert.Equal("syntax", r.Errors.Single().Message);
    }

    [Fact]
    public async Task Non_graphql_error_body_throws_http_error()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.Unauthorized, "denied");
        using var sp = Fast.Build(h);
        await Assert.ThrowsAsync<HttpRequestException>(() => sp.GetRequiredService<IWireGraphQlClient>().ExecuteAsync<Data>(Endpoint, new GraphQlRequest("query { x }")));
    }

    [Fact]
    public async Task Custom_headers_are_sent()
    {
        var h = new ScriptedHandler().Then(HttpStatusCode.OK, """{"data":{"viewer":{"login":"a"}}}""");
        using var sp = Fast.Build(h);
        await sp.GetRequiredService<IWireGraphQlClient>().ExecuteAsync<Data>(Endpoint, new GraphQlRequest("query { x }"),
            new Dictionary<string, string> { ["Authorization"] = "Bearer t" });
        Assert.Equal("Bearer t", h.Requests[0].Headers.GetValues("Authorization").Single());
    }
}
