using System.Net;
using System.Net.Http.Json;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers.BrightData;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Providers;

public sealed class BrightDataAdapterTests
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

    private static (BrightDataAdapter Adapter, StubHandler Handler) CreateSut(HttpResponseMessage response)
    {
        var handler = new StubHandler(response);
        var services = new ServiceCollection();
        services.AddHttpClient("ProxyProvider:BrightData").ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return (new BrightDataAdapter(provider.GetRequiredService<IHttpClientFactory>()), handler);
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_MapIpsToProviderProxyRecords_And_UseBearerToken()
    {
        var payload = new BrightDataZoneIpsResponse([new BrightDataIpRecord("9.9.9.9", 22225, "cust1", "residential_cl", "zone-pass")]);
        var (sut, handler) = CreateSut(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, "{\"ApiToken\":\"token-1\",\"Zone\":\"residential_cl\"}", CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Single().Host.ShouldBe("9.9.9.9");
        result.Proxies.Single().Username.ShouldBe("cust1-zone-residential_cl");
        handler.LastRequest!.Headers.Authorization!.ToString().ShouldBe("Bearer token-1");
        handler.LastRequest!.RequestUri!.Query.ShouldContain("zone=residential_cl");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_ResponseIsNotSuccessful()
    {
        var (sut, _) = CreateSut(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, "{\"ApiToken\":\"bad\",\"Zone\":\"z\"}", CancellationToken.None);

        result.Success.ShouldBeFalse();
    }

    [Fact]
    public void SupportsRenew_Should_BeFalse() =>
        new BrightDataAdapter(Substitute.For<IHttpClientFactory>()).SupportsRenew.ShouldBeFalse();
}
