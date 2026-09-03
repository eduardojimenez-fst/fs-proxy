using System.Net.Http.Json;
using System.Text.Json;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers.WebShare;

/// <summary>
/// WebShare's public API exposes proxy list/replace at the plan level, not a per-proxy
/// "rotate this exact IP" call, so <see cref="RenewProxyAsync"/> always reports unsupported
/// and renewal falls back to the admin-notification path (Task 19) until a real rotate
/// endpoint is confirmed against https://apidocs.webshare.io.
/// </summary>
public sealed class WebShareAdapter(IHttpClientFactory httpClientFactory) : IProxyProviderAdapter
{
    private const string ClientName = "ProxyProvider:WebShare";

    public ProxyProviderType ProviderType => ProxyProviderType.WebShare;
    public bool SupportsSync => true;
    public bool SupportsRenew => false;

    public async Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var credentials = JsonSerializer.Deserialize<WebShareCredentials>(decryptedCredentials)
            ?? throw new InvalidOperationException("WebShare credentials could not be parsed.");

        using var client = httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://proxy.webshare.io/api/v2/proxy/list/?mode=direct&page_size=100");
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {credentials.ApiKey}");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ProviderSyncResult.Failed($"WebShare returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<WebShareProxyListResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("WebShare returned an empty proxy list response.");

        var proxies = payload.Results
            .Where(r => r.Valid)
            .Select(r => new ProviderProxyRecord(r.Id, r.ProxyAddress, r.Port, ProxyProtocol.Http, r.Username, r.Password, IsActive: true))
            .ToList();

        return ProviderSyncResult.Ok(proxies);
    }

    public Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderRenewResult.Unsupported());
}
