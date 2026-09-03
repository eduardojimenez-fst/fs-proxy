using System.Net;
using System.Net.Http.Json;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers.Oxylabs;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Providers;

public sealed class OxylabsAdapterTests
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

    private static (OxylabsAdapter Adapter, StubHandler Handler) CreateSut(HttpResponseMessage response)
    {
        var handler = new StubHandler(response);
        var services = new ServiceCollection();
        services.AddHttpClient("ProxyProvider:Oxylabs").ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return (new OxylabsAdapter(provider.GetRequiredService<IHttpClientFactory>()), handler);
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_MapResultsToProviderProxyRecords_And_UseBasicAuth()
    {
        var payload = new OxylabsProxyListResponse([new OxylabsProxyRecord("ext-9", "5.6.7.8", 60000, "oxy-user", "oxy-pass", "active")]);
        var (sut, handler) = CreateSut(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        var account = ProviderAccount.Create("Oxylabs", ProxyProviderType.Oxylabs, "n/a");

        var result = await sut.SyncProxiesAsync(account, "{\"Username\":\"acct\",\"Password\":\"secret\"}", CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Single().Host.ShouldBe("5.6.7.8");
        handler.LastRequest!.Headers.Authorization!.Scheme.ShouldBe("Basic");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ExcludeNonActiveProxies()
    {
        var payload = new OxylabsProxyListResponse([
            new OxylabsProxyRecord("ext-1", "1.1.1.1", 60000, "u", "p", "active"),
            new OxylabsProxyRecord("ext-2", "2.2.2.2", 60000, "u", "p", "suspended")]);
        var (sut, _) = CreateSut(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        var account = ProviderAccount.Create("Oxylabs", ProxyProviderType.Oxylabs, "n/a");

        var result = await sut.SyncProxiesAsync(account, "{\"Username\":\"acct\",\"Password\":\"secret\"}", CancellationToken.None);

        result.Proxies.Select(p => p.ExternalId).ShouldBe(["ext-1"]);
    }
}
