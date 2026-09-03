using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers.Oxylabs;

/// <summary>
/// Oxylabs' dedicated/ISP proxy list endpoint is confirmed against public documentation, but a
/// per-IP rotate call isn't, so <see cref="RenewProxyAsync"/> always reports unsupported and
/// renewal falls back to the admin-notification path (Task 19) until a real rotate endpoint is
/// confirmed. Unlike WebShare, Oxylabs authenticates with HTTP Basic auth (account username and
/// password) rather than a bearer/token header.
/// </summary>
public sealed class OxylabsAdapter(IHttpClientFactory httpClientFactory) : IProxyProviderAdapter
{
    private const string ClientName = "ProxyProvider:Oxylabs";

    public ProxyProviderType ProviderType => ProxyProviderType.Oxylabs;
    public bool SupportsSync => true;
    public bool SupportsRenew => false;

    public async Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var credentials = JsonSerializer.Deserialize<OxylabsCredentials>(decryptedCredentials)
            ?? throw new InvalidOperationException("Oxylabs credentials could not be parsed.");

        using var client = httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.oxylabs.io/v1/proxies");
        var basicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicToken);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ProviderSyncResult.Failed($"Oxylabs returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<OxylabsProxyListResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Oxylabs returned an empty proxy list response.");

        var proxies = payload.Results
            .Where(r => string.Equals(r.Status, "active", StringComparison.OrdinalIgnoreCase))
            .Select(r => new ProviderProxyRecord(r.Id, r.Ip, r.Port, ProxyProtocol.Http, r.Username, r.Password, IsActive: true))
            .ToList();

        return ProviderSyncResult.Ok(proxies);
    }

    public Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderRenewResult.Unsupported());
}
