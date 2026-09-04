using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers.BrightData;

/// <summary>
/// BrightData organizes proxies into "zones", not individual per-IP accounts. A zone with an
/// enumerable IP roster (static, whether single- or multi-country) yields one record per IP,
/// each pinned via a "-ip-{ip}" username suffix; a zone with no enumerable roster (rotating)
/// yields a single record representing the whole zone/gateway, with no IP pin — BrightData
/// rotates internally. Verified against a real production zone export: connection is always
/// through the shared gateway host:port, never a literal per-IP socket. See
/// docs/superpowers/specs/2026-09-03-provider-sync-brightdata-webshare-design.md.
/// </summary>
public sealed class BrightDataAdapter(IHttpClientFactory httpClientFactory) : IProxyProviderAdapter
{
    private const string ClientName = "ProxyProvider:BrightData";

    public ProxyProviderType ProviderType => ProxyProviderType.BrightData;
    public bool SupportsSync => true;
    public bool SupportsRenew => false;

    public async Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        BrightDataCredentials? credentials;
        try
        {
            credentials = JsonSerializer.Deserialize<BrightDataCredentials>(decryptedCredentials);
        }
        catch (JsonException ex)
        {
            return ProviderSyncResult.Failed($"Invalid credentials JSON: {ex.Message}");
        }

        if (credentials is null)
        {
            return ProviderSyncResult.Failed("Invalid credentials JSON: BrightData credentials could not be parsed.");
        }

        using var client = httpClientFactory.CreateClient(ClientName);

        using var zoneRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.brightdata.com/zone?zone={Uri.EscapeDataString(credentials.Zone)}");
        zoneRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ApiToken);
        using var zoneResponse = await client.SendAsync(zoneRequest, cancellationToken).ConfigureAwait(false);
        if (!zoneResponse.IsSuccessStatusCode)
        {
            return ProviderSyncResult.Failed($"BrightData returned {(int)zoneResponse.StatusCode} {zoneResponse.ReasonPhrase} for zone config.");
        }

        var zoneConfig = await zoneResponse.Content.ReadFromJsonAsync<BrightDataZoneConfigResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("BrightData returned an empty zone config response.");
        if (zoneConfig.Password.Count == 0)
        {
            return ProviderSyncResult.Failed("BrightData zone config did not include a password.");
        }
        var password = zoneConfig.Password[0];

        using var ipsRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.brightdata.com/zone/ips?zone={Uri.EscapeDataString(credentials.Zone)}");
        ipsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ApiToken);
        using var ipsResponse = await client.SendAsync(ipsRequest, cancellationToken).ConfigureAwait(false);

        if (ipsResponse.StatusCode == HttpStatusCode.BadRequest)
        {
            var poolCountry = SingleCountryOrNull(zoneConfig.Plan.DefaultCountry ?? zoneConfig.Plan.Country);
            var poolUsername = $"brd-customer-{credentials.CustomerId}-zone-{credentials.Zone}";
            return ProviderSyncResult.Ok([
                new ProviderProxyRecord($"{credentials.Zone}:pool", credentials.GatewayHost, credentials.GatewayPort, ProxyProtocol.Http,
                    poolUsername, password, IsActive: true, Country: poolCountry, ProviderGrouping: credentials.Zone)
            ]);
        }

        if (!ipsResponse.IsSuccessStatusCode)
        {
            return ProviderSyncResult.Failed($"BrightData returned {(int)ipsResponse.StatusCode} {ipsResponse.ReasonPhrase} for zone IPs.");
        }

        var ipsPayload = await ipsResponse.Content.ReadFromJsonAsync<BrightDataZoneIpsResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("BrightData returned an empty zone IPs response.");

        var proxies = ipsPayload.Ips
            .Select(ip => new ProviderProxyRecord(
                ExternalId: $"{credentials.Zone}:{ip.Ip}",
                Host: credentials.GatewayHost,
                Port: credentials.GatewayPort,
                Protocol: ProxyProtocol.Http,
                Username: $"brd-customer-{credentials.CustomerId}-zone-{credentials.Zone}-ip-{ip.Ip}",
                Password: password,
                IsActive: true,
                Country: ip.Maxmind,
                ProviderGrouping: credentials.Zone))
            .ToList();

        return ProviderSyncResult.Ok(proxies);
    }

    /// <summary>
    /// BrightData reports a multi-country zone's countries as a single space-separated string
    /// (e.g. "ar us") with no per-IP breakdown available in the rotating (pool) case — there is
    /// no single correct country to attribute, so this returns null rather than guessing.
    /// </summary>
    private static string? SingleCountryOrNull(string? country) =>
        string.IsNullOrWhiteSpace(country) || country.Contains(' ', StringComparison.Ordinal) ? null : country;

    public Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderRenewResult.Unsupported());
}
