using System.Net;
using System.Net.Http.Json;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers.WebShare;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Providers;

public sealed class WebShareAdapterTests
{
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private static (WebShareAdapter Adapter, StubHandler Handler) CreateSut(HttpResponseMessage response)
    {
        var handler = new StubHandler(response);
        var services = new ServiceCollection();
        services.AddHttpClient("ProxyProvider:WebShare").ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return (new WebShareAdapter(provider.GetRequiredService<IHttpClientFactory>()), handler);
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_MapResultsToProviderProxyRecords()
    {
        var payload = new WebShareProxyListResponse(1, null, [new WebShareProxyRecord("ext-1", "user", "pass", "1.2.3.4", 8080, true)]);
        var (sut, handler) = CreateSut(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "n/a");

        var result = await sut.SyncProxiesAsync(account, /* decrypted */ "{\"ApiKey\":\"key-123\"}", CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Single().Host.ShouldBe("1.2.3.4");
        result.Proxies.Single().ExternalId.ShouldBe("ext-1");
        handler.LastRequest!.Headers.Authorization!.ToString().ShouldBe("Token key-123");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_ResponseIsNotSuccessful()
    {
        var (sut, _) = CreateSut(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "n/a");

        var result = await sut.SyncProxiesAsync(account, "{\"ApiKey\":\"bad-key\"}", CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SupportsRenew_Should_BeFalse() =>
        new WebShareAdapter(Substitute.For<IHttpClientFactory>()).SupportsRenew.ShouldBeFalse();
}
