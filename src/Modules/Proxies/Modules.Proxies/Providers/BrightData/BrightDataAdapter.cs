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

    private static readonly JsonSerializerOptions CredentialsJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        // Malformed stored credentials (e.g. the admin UI's own placeholder pasted verbatim, or a
        // JSON body missing a required key) must surface as a normal sync failure, not an unhandled
        // exception: only the Failed path reaches ProviderAccountSyncService's
        // RecordSyncResult(success: false, ...) and so increments ConsecutiveSyncFailures towards
        // the admin notification threshold, matching WebShareAdapter's precedent. Deserialization is
        // case-insensitive (JsonSerializerDefaults.Web) because the admin UI's Provider Account
        // dialog shows a camelCase example ({"apiToken":...}), which would otherwise silently
        // produce an all-null/all-default record under the framework's default case-sensitive
        // matching; the blank-field guard below is defense-in-depth against ANY malformed or
        // incomplete credentials JSON, camelCase or not.
        BrightDataCredentials? credentials;
        try
        {
            credentials = JsonSerializer.Deserialize<BrightDataCredentials>(decryptedCredentials, CredentialsJsonOptions);
        }
        catch (JsonException ex)
        {
            return ProviderSyncResult.Failed($"Invalid credentials JSON: {ex.Message}");
        }

        if (credentials is null)
        {
            return ProviderSyncResult.Failed("Invalid credentials JSON: BrightData credentials could not be parsed.");
        }

        if (string.IsNullOrWhiteSpace(credentials.ApiToken) || string.IsNullOrWhiteSpace(credentials.Zone) ||
            string.IsNullOrWhiteSpace(credentials.CustomerId) || credentials.GatewayPort <= 0)
        {
            return ProviderSyncResult.Failed("Invalid credentials JSON: BrightData credentials are missing required fields (apiToken, zone, customerId, gatewayPort).");
        }

        using var client = httpClientFactory.CreateClient(ClientName);

        using var zoneRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.brightdata.com/zone?zone={Uri.EscapeDataString(credentials.Zone)}");
        zoneRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ApiToken);
        using var zoneResponse = await client.SendAsync(zoneRequest, cancellationToken).ConfigureAwait(false);
        if (!zoneResponse.IsSuccessStatusCode)
        {
            return ProviderSyncResult.Failed($"BrightData returned {(int)zoneResponse.StatusCode} {zoneResponse.ReasonPhrase} for zone config.");
        }

        BrightDataZoneConfigResponse? zoneConfig;
        try
        {
            zoneConfig = await zoneResponse.Content.ReadFromJsonAsync<BrightDataZoneConfigResponse>(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("BrightData returned an empty zone config response.");
        }
        catch (JsonException ex)
        {
            return ProviderSyncResult.Failed($"BrightData returned an unparseable zone config response: {ex.Message}");
        }

        if (zoneConfig.Password is not { Count: > 0 })
        {
            return ProviderSyncResult.Failed("BrightData zone config did not include a password.");
        }
        var password = zoneConfig.Password[0];

        using var ipsRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.brightdata.com/zone/ips?zone={Uri.EscapeDataString(credentials.Zone)}");
        ipsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ApiToken);
        using var ipsResponse = await client.SendAsync(ipsRequest, cancellationToken).ConfigureAwait(false);

        if (ipsResponse.StatusCode == HttpStatusCode.BadRequest)
        {
            var poolGeolocation = TruncateToNullIfTooLong(SingleGeolocationOrNull(zoneConfig.Plan?.DefaultCountry ?? zoneConfig.Plan?.Country));
            var poolUsername = $"brd-customer-{credentials.CustomerId}-zone-{credentials.Zone}";
            return ProviderSyncResult.Ok([
                new ProviderProxyRecord($"{credentials.Zone}:pool", credentials.GatewayHost, credentials.GatewayPort, ProxyProtocol.Http,
                    poolUsername, password, IsActive: true, Geolocation: poolGeolocation, ProviderGrouping: credentials.Zone)
            ]);
        }

        if (!ipsResponse.IsSuccessStatusCode)
        {
            return ProviderSyncResult.Failed($"BrightData returned {(int)ipsResponse.StatusCode} {ipsResponse.ReasonPhrase} for zone IPs.");
        }

        BrightDataZoneIpsResponse? ipsPayload;
        try
        {
            ipsPayload = await ipsResponse.Content.ReadFromJsonAsync<BrightDataZoneIpsResponse>(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("BrightData returned an empty zone IPs response.");
        }
        catch (JsonException ex)
        {
            return ProviderSyncResult.Failed($"BrightData returned an unparseable zone IPs response: {ex.Message}");
        }

        var proxies = ipsPayload.Ips
            .Select(ip => new ProviderProxyRecord(
                ExternalId: $"{credentials.Zone}:{ip.Ip}",
                Host: credentials.GatewayHost,
                Port: credentials.GatewayPort,
                Protocol: ProxyProtocol.Http,
                Username: $"brd-customer-{credentials.CustomerId}-zone-{credentials.Zone}-ip-{ip.Ip}",
                Password: password,
                IsActive: true,
                Geolocation: TruncateToNullIfTooLong(ip.Maxmind),
                ProviderGrouping: credentials.Zone))
            .ToList();

        return ProviderSyncResult.Ok(proxies);
    }

    /// <summary>
    /// BrightData reports a multi-country zone's countries as a single space-separated string
    /// (e.g. "ar us") with no per-IP breakdown available in the rotating (pool) case — there is
    /// no single correct country to attribute, so this returns null rather than guessing.
    /// </summary>
    private static string? SingleGeolocationOrNull(string? geolocation) =>
        string.IsNullOrWhiteSpace(geolocation) || geolocation.Contains(' ', StringComparison.Ordinal) ? null : geolocation;

    /// <summary>
    /// <see cref="Domain.Proxy.Geolocation"/> is a <c>varchar(10)</c> column. A provider-reported
    /// geolocation value that's longer than that (an unexpected delimiter, a full name instead of
    /// an ISO2 code, etc.) would pass every InMemory-backed test and then fail with a Postgres
    /// 22001 "value too long" error on SaveChanges in production. Preferring null over truncating
    /// or throwing avoids both silently corrupting the value and crashing a sync over a field
    /// that's informational-only.
    /// </summary>
    private static string? TruncateToNullIfTooLong(string? geolocation) =>
        geolocation is { Length: > 10 } ? null : geolocation;

    public Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderRenewResult.Unsupported());
}
