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

    private sealed class SequencedStubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responses[_index++]);
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

    [Theory]
    [InlineData("raw-api-key-not-json")]
    [InlineData("{\"ApiKey\":")]
    [InlineData("")]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_CredentialsAreNotValidJson(string credentials)
    {
        var (sut, _) = CreateSut(new HttpResponseMessage(HttpStatusCode.OK));
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "n/a");

        var result = await sut.SyncProxiesAsync(account, credentials, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldStartWith("Invalid credentials JSON");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_CredentialsAreJsonNull()
    {
        var (sut, _) = CreateSut(new HttpResponseMessage(HttpStatusCode.OK));
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "n/a");

        var result = await sut.SyncProxiesAsync(account, "null", CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldStartWith("Invalid credentials JSON");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_FollowPagination_And_MapCountryAndProviderGrouping()
    {
        var page1 = new WebShareProxyListResponse(2, "https://proxy.webshare.io/api/v2/proxy/list/?mode=direct&page=2&page_size=100",
            [new WebShareProxyRecord("ext-1", "user", "pass", "1.2.3.4", 8080, true, "CL")]);
        var page2 = new WebShareProxyListResponse(2, null,
            [new WebShareProxyRecord("ext-2", "user", "pass", "5.6.7.8", 8081, true, "AR")]);
        var handler = new SequencedStubHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(page1) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(page2) });
        var services = new ServiceCollection();
        services.AddHttpClient("ProxyProvider:WebShare").ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        var sut = new WebShareAdapter(provider.GetRequiredService<IHttpClientFactory>());
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "n/a");

        var result = await sut.SyncProxiesAsync(account, "{\"ApiKey\":\"key-123\"}", CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Count.ShouldBe(2);
        handler.Requests.Count.ShouldBe(2);
        var first = result.Proxies.Single(p => p.ExternalId == "ext-1");
        first.Country.ShouldBe("CL");
        first.ProviderGrouping.ShouldBe("Proxy List");
        result.Proxies.Single(p => p.ExternalId == "ext-2").Country.ShouldBe("AR");
    }

    [Fact]
    public void SupportsRenew_Should_BeFalse() =>
        new WebShareAdapter(Substitute.For<IHttpClientFactory>()).SupportsRenew.ShouldBeFalse();
}
