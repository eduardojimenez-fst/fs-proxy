using FSH.Modules.Proxies.Contracts.Dtos;
using Integration.Tests.Infrastructure;
#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Integration.Tests.Tests.Proxies;

/// <summary>
/// End-to-end cover for <c>POST /api/v1/proxies/feedback/batch</c>. Proves three things the handler
/// unit tests cannot: that <c>ApiKeyAuthenticationHandler</c> admits an <c>X-Api-Key</c>-only request,
/// that <c>ProxiesConsumerAuthorizationHandler</c> does not reject an API-key principal, and that the
/// route does not collide with the sibling <c>POST /{id:guid}/feedback</c>.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class ProxyFeedbackBatchTests
{
    private const string ProxiesBasePath = "/api/v1/proxies";

    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public ProxyFeedbackBatchTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task PostBatch_Should_AcceptKnownProxies_And_RejectUnknown_UnderApiKeyAuth()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();

        // 1. Issue an API key. The plaintext key is returned exactly once — here.
        var apiClientResponse = await admin.PostAsJsonAsync(
            $"{ProxiesBasePath}/api-clients",
            new { name = $"batch-feedback-test-{Guid.NewGuid():N}" });
        apiClientResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var apiClient = await apiClientResponse.Content.ReadFromJsonAsync<CreateApiClientResult>();
        apiClient.ShouldNotBeNull();

        // 2. Create a real proxy to report against. Proxy.Create leaves it in Testing status, which is
        //    fine: the batch handler checks existence, not status.
        var proxyResponse = await admin.PostAsJsonAsync($"{ProxiesBasePath}/manual-proxies", new
        {
            host = "203.0.113.10",
            port = 8080,
            protocol = "Http",
            username = (string?)null,
            plaintextPassword = (string?)null,
            tagNames = Array.Empty<string>(),
        });
        proxyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var proxyId = await proxyResponse.Content.ReadFromJsonAsync<Guid>();

        // 3. A client carrying ONLY the API key — no bearer token.
        using var consumer = _factory.CreateClient();
        consumer.DefaultRequestHeaders.Add("X-Api-Key", apiClient.PlaintextKey);

        var unknownProxyId = Guid.NewGuid();
        var batchResponse = await consumer.PostAsJsonAsync($"{ProxiesBasePath}/feedback/batch", new
        {
            events = new[]
            {
                new { proxyId, outcome = "Banned", detail = (string?)"mercadopublico:robot-check" },
                new { proxyId, outcome = "Timeout", detail = (string?)null },
                new { proxyId, outcome = "Success", detail = (string?)null },
                new { proxyId = unknownProxyId, outcome = "Failure", detail = (string?)null },
            },
        });

        batchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await batchResponse.Content.ReadFromJsonAsync<BatchFeedbackResult>();
        result.ShouldNotBeNull();
        result.Accepted.ShouldBe(3);
        result.Rejected.ShouldBe([unknownProxyId]);
    }

    [Fact]
    public async Task PostBatch_Should_Return401_When_ApiKeyIsMissing()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync($"{ProxiesBasePath}/feedback/batch", new
        {
            events = new[]
            {
                new { proxyId = Guid.NewGuid(), outcome = "Banned", detail = (string?)null },
            },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
