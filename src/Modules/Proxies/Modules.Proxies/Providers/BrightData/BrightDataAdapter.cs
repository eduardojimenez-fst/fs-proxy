using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers.BrightData;

public sealed class BrightDataAdapter(IHttpClientFactory httpClientFactory) : IProxyProviderAdapter
{
    private const string ClientName = "ProxyProvider:BrightData";

    public ProxyProviderType ProviderType => ProxyProviderType.BrightData;
    public bool SupportsSync => true;
    public bool SupportsRenew => false;

    public async Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var credentials = JsonSerializer.Deserialize<BrightDataCredentials>(decryptedCredentials)
            ?? throw new InvalidOperationException("BrightData credentials could not be parsed.");

        using var client = httpClientFactory.CreateClient(ClientName);
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["zone"] = credentials.Zone;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.brightdata.com/zone/ips?{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ApiToken);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ProviderSyncResult.Failed($"BrightData returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<BrightDataZoneIpsResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("BrightData returned an empty zone IPs response.");

        var proxies = payload.Ips
            .Select(ip => new ProviderProxyRecord(
                ExternalId: $"{ip.Zone}:{ip.Ip}:{ip.Port}",
                Host: ip.Ip,
                Port: ip.Port,
                Protocol: ProxyProtocol.Http,
                Username: $"{ip.Customer}-zone-{ip.Zone}",
                Password: ip.Password,
                IsActive: true))
            .ToList();

        return ProviderSyncResult.Ok(proxies);
    }

    public Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderRenewResult.Unsupported());
}
