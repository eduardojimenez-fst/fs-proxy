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
    private const string ValidCredentials =
        "{\"ApiToken\":\"token-1\",\"Zone\":\"zone1\",\"CustomerId\":\"cust1\",\"GatewayPort\":44445,\"GatewayHost\":\"brd.superproxy.io\"}";

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

    private static (BrightDataAdapter Adapter, SequencedStubHandler Handler) CreateSut(params HttpResponseMessage[] responses)
    {
        var handler = new SequencedStubHandler(responses);
        var services = new ServiceCollection();
        services.AddHttpClient("ProxyProvider:BrightData").ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return (new BrightDataAdapter(provider.GetRequiredService<IHttpClientFactory>()), handler);
    }

    private static HttpResponseMessage ZoneConfigResponse(string password, string? country = null, string? defaultCountry = null) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new BrightDataZoneConfigResponse([password], new BrightDataZonePlan(country, defaultCountry)))
        };

    [Fact]
    public async Task SyncProxiesAsync_Should_MapOneRecordPerIp_When_ZoneHasEnumerableIps()
    {
        var ipsResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new BrightDataZoneIpsResponse([
                new BrightDataZoneIpRecord("9.9.9.9", "cl"),
                new BrightDataZoneIpRecord("8.8.8.8", "ar")]))
        };
        var (sut, handler) = CreateSut(ZoneConfigResponse("zone-pass"), ipsResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Count.ShouldBe(2);
        var first = result.Proxies.Single(p => p.Host == "brd.superproxy.io" && p.Port == 44445 && p.Country == "cl");
        first.Username.ShouldBe("brd-customer-cust1-zone-zone1-ip-9.9.9.9");
        first.Password.ShouldBe("zone-pass");
        first.ProviderGrouping.ShouldBe("zone1");
        result.Proxies.Single(p => p.Country == "ar").Username.ShouldBe("brd-customer-cust1-zone-zone1-ip-8.8.8.8");
        handler.Requests[0].RequestUri!.ToString().ShouldContain("/zone?zone=zone1");
        handler.Requests[1].RequestUri!.ToString().ShouldContain("/zone/ips?zone=zone1");
        handler.Requests[0].Headers.Authorization!.ToString().ShouldBe("Bearer token-1");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_MapSingleZoneRecord_When_ZoneIsRotating()
    {
        var rotatingResponse = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Wrong zone plan")
        };
        var (sut, _) = CreateSut(ZoneConfigResponse("zone-pass", defaultCountry: "cl"), rotatingResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeTrue();
        var pool = result.Proxies.Single();
        pool.Username.ShouldBe("brd-customer-cust1-zone-zone1");
        pool.Password.ShouldBe("zone-pass");
        pool.Country.ShouldBe("cl");
        pool.ProviderGrouping.ShouldBe("zone1");
        pool.ExternalId.ShouldBe("zone1:pool");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_LeaveCountryNull_When_ZonePlanCountryIsMultiValued()
    {
        var rotatingResponse = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Wrong zone plan") };
        var (sut, _) = CreateSut(ZoneConfigResponse("zone-pass", country: "ar us"), rotatingResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Proxies.Single().Country.ShouldBeNull();
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_ZoneConfigRequestFails()
    {
        var (sut, _) = CreateSut(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeFalse();
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_IpsRequestFailsWithNonBadRequestError()
    {
        var (sut, _) = CreateSut(ZoneConfigResponse("zone-pass"), new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeFalse();
    }

    [Theory]
    [InlineData("raw-api-token-not-json")]
    [InlineData("{\"ApiToken\":")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"apiToken\":\"\",\"zone\":\"zone1\",\"customerId\":\"cust1\",\"gatewayPort\":44445}")]
    [InlineData("{\"apiToken\":\"token-1\",\"zone\":\"zone1\",\"customerId\":\"cust1\",\"gatewayPort\":0}")]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_CredentialsAreNotValidJson(string credentials)
    {
        var (sut, _) = CreateSut(new HttpResponseMessage(HttpStatusCode.OK));
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, credentials, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldStartWith("Invalid credentials JSON");
    }

    [Fact]
    public void SupportsRenew_Should_BeFalse() =>
        new BrightDataAdapter(Substitute.For<IHttpClientFactory>()).SupportsRenew.ShouldBeFalse();

    // The tests above build every fixture from the same C# response record + [JsonPropertyName]
    // attributes the adapter deserializes with, so a typo in a JSON property name would round-trip
    // symmetrically and still pass. These two use raw JSON string literals — matching the real,
    // empirically-captured shapes from the design spec and the admin UI's own placeholder — to
    // actually exercise the property-name contract.
    [Fact]
    public async Task SyncProxiesAsync_Should_ParseRealZoneIpsJsonShape()
    {
        var ipsJson = """{"ips":[{"ip":"9.9.9.9","maxmind":"cl","ext":{}}]}""";
        var ipsResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ipsJson, System.Text.Encoding.UTF8, "application/json")
        };
        var (sut, _) = CreateSut(ZoneConfigResponse("zone-pass"), ipsResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Single().Country.ShouldBe("cl");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_AcceptCamelCaseCredentialsJson()
    {
        var camelCaseCredentials = """{"apiToken":"token-1","zone":"zone1","customerId":"cust1","gatewayPort":44445}""";
        var ipsResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new BrightDataZoneIpsResponse([]))
        };
        var (sut, handler) = CreateSut(ZoneConfigResponse("zone-pass"), ipsResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, camelCaseCredentials, CancellationToken.None);

        result.Success.ShouldBeTrue();
        handler.Requests[0].RequestUri!.ToString().ShouldContain("/zone?zone=zone1");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"password\":null}")]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_ZoneConfigPasswordIsMissingOrNull(string zoneConfigJson)
    {
        var zoneConfigResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(zoneConfigJson, System.Text.Encoding.UTF8, "application/json")
        };
        var (sut, _) = CreateSut(zoneConfigResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("BrightData zone config did not include a password.");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_LeaveCountryNull_When_RotatingZonePlanIsMissing()
    {
        // "plan" absent entirely (not just its sub-fields) — Plan is a non-nullable record type,
        // so this can only be produced via a raw JSON literal, not the C# fixture helper.
        var zoneConfigJson = """{"password":["zone-pass"]}""";
        var zoneConfigResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(zoneConfigJson, System.Text.Encoding.UTF8, "application/json")
        };
        var rotatingResponse = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Wrong zone plan") };
        var (sut, _) = CreateSut(zoneConfigResponse, rotatingResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Single().Country.ShouldBeNull();
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_ZoneConfigBodyIsUnparseableJson()
    {
        var zoneConfigResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json-at-all-{{{", System.Text.Encoding.UTF8, "application/json")
        };
        var (sut, _) = CreateSut(zoneConfigResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldStartWith("BrightData returned an unparseable zone config response");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_ZoneIpsBodyIsUnparseableJson()
    {
        var ipsResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json-at-all-{{{", System.Text.Encoding.UTF8, "application/json")
        };
        var (sut, _) = CreateSut(ZoneConfigResponse("zone-pass"), ipsResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldStartWith("BrightData returned an unparseable zone IPs response");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_NullOutCountry_When_PerIpMaxmindExceedsColumnLength()
    {
        var ipsResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new BrightDataZoneIpsResponse([
                new BrightDataZoneIpRecord("9.9.9.9", "way-too-long-country-value")]))
        };
        var (sut, _) = CreateSut(ZoneConfigResponse("zone-pass"), ipsResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Single().Country.ShouldBeNull();
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_NullOutCountry_When_PoolCountryExceedsColumnLength()
    {
        var rotatingResponse = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Wrong zone plan") };
        var (sut, _) = CreateSut(ZoneConfigResponse("zone-pass", defaultCountry: "way-too-long"), rotatingResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Single().Country.ShouldBeNull();
    }
}
