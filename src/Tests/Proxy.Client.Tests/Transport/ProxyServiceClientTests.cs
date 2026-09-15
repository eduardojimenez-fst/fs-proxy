using System.Net;
using FSH.Proxy.Client;
using FSH.Proxy.Client.Transport;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests.Transport;

public sealed class ProxyServiceClientTests
{
    private static ProxyClientOptions Options() => new()
    {
        BaseAddress = new Uri("https://proxy.test"),
        ApiKey = "fsh_proxies_deadbeef",
    };

    [Fact]
    public async Task RequestAsync_Should_Send_The_ApiKey_Header_And_Normalized_Tags()
    {
        const string body = """
        [{"id":"11111111-1111-1111-1111-111111111111","host":"203.0.113.10","port":8080,"protocol":"Http","username":"u","password":"p"}]
        """;
        var handler = new StubHandler(HttpStatusCode.OK, body);
        using var http = new HttpClient(handler);
        var sut = new ProxyServiceClient(http, Options());

        var result = await sut.RequestAsync(["Country:CL", " entityType:Tender "], 25, CancellationToken.None);

        handler.LastRequest!.Headers.GetValues("X-Api-Key").ShouldBe(["fsh_proxies_deadbeef"]);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/api/v1/proxies/request");
        // Tags must go out normalized — the server lowercases on its side, but sending the raw
        // form makes the request body a poor match for what the admin UI shows.
        handler.LastRequestBody.ShouldContain("country:cl");
        handler.LastRequestBody.ShouldContain("entitytype:tender");
        result.Count.ShouldBe(1);
        result[0].Host.ShouldBe("203.0.113.10");
        result[0].Password.ShouldBe("p");
    }

    [Fact]
    public async Task RequestAsync_Should_Return_Empty_When_The_Service_Says_No_Proxies_Match()
    {
        // The service answers 404 with a ProblemDetails when no Active proxy matches the tags.
        // That is an ordinary, expected state — not an exception — because the caller's fallback
        // (keep serving the stale snapshot) is the same either way.
        var handler = new StubHandler(HttpStatusCode.NotFound, """{"title":"No active proxies match the requested tags."}""");
        using var http = new HttpClient(handler);
        var sut = new ProxyServiceClient(http, Options());

        var result = await sut.RequestAsync(["country:cl"], 25, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task RequestAsync_Should_Throw_On_An_Unauthorized_Response()
    {
        // A bad API key is a misconfiguration the operator must see, not something to swallow.
        var handler = new StubHandler(HttpStatusCode.Unauthorized, "");
        using var http = new HttpClient(handler);
        var sut = new ProxyServiceClient(http, Options());

        await Should.ThrowAsync<HttpRequestException>(
            () => sut.RequestAsync(["country:cl"], 25, CancellationToken.None));
    }

    [Fact]
    public async Task RequestFeedbackAsync_Should_Post_Outcomes_As_Strings()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"accepted":1,"rejected":[]}""");
        using var http = new HttpClient(handler);
        var sut = new ProxyServiceClient(http, Options());
        var proxyId = Guid.NewGuid();

        await sut.RequestFeedbackAsync(
            [new FeedbackItem(proxyId, ProxyOutcome.Banned, "mercadopublico:robot-check")],
            CancellationToken.None);

        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/api/v1/proxies/feedback/batch");
        // The service registers JsonStringEnumConverter, so the outcome must go out as a NAME.
        // Sending the numeric value deserializes to the wrong member without any error.
        handler.LastRequestBody.ShouldContain("\"Banned\"");
        handler.LastRequestBody.ShouldContain(proxyId.ToString());
    }

    [Fact]
    public async Task RequestFeedbackAsync_Should_Not_Call_The_Service_For_An_Empty_Batch()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "");
        using var http = new HttpClient(handler);
        var sut = new ProxyServiceClient(http, Options());

        await sut.RequestFeedbackAsync([], CancellationToken.None);

        handler.CallCount.ShouldBe(0);
    }
}
