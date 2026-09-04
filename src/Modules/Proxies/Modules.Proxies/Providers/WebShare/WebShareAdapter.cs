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

    // The admin UI's credentials placeholder shows {"apiKey":"..."} (camelCase); default
    // System.Text.Json options are case-sensitive against WebShareCredentials(string ApiKey),
    // so pasting the documented shape silently yields a null ApiKey (Authorization: Token <empty>)
    // instead of a decode failure — confirmed live as WebShare returning 401 with no visible parse error.
    private static readonly JsonSerializerOptions CredentialsJsonOptions = new(JsonSerializerDefaults.Web);

    public ProxyProviderType ProviderType => ProxyProviderType.WebShare;
    public bool SupportsSync => true;
    public bool SupportsRenew => false;

    public async Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        // Malformed stored credentials (e.g. an admin pasted a bare API key instead of the
        // expected {"ApiKey":"..."} JSON) must surface as a normal sync failure, not an unhandled
        // exception: only the Failed path reaches ProviderAccountSyncService's
        // RecordSyncResult(success: false, ...) and so increments ConsecutiveSyncFailures towards
        // the admin notification threshold.
        WebShareCredentials? credentials;
        try
        {
            credentials = JsonSerializer.Deserialize<WebShareCredentials>(decryptedCredentials, CredentialsJsonOptions);
        }
        catch (JsonException ex)
        {
            return ProviderSyncResult.Failed($"Invalid credentials JSON: {ex.Message}");
        }

        if (credentials is null)
        {
            return ProviderSyncResult.Failed("Invalid credentials JSON: WebShare credentials could not be parsed.");
        }

        if (string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            return ProviderSyncResult.Failed("Invalid credentials JSON: WebShare ApiKey is missing or blank.");
        }

        using var client = httpClientFactory.CreateClient(ClientName);
        var proxies = new List<ProviderProxyRecord>();
        string? nextUrl = "https://proxy.webshare.io/api/v2/proxy/list/?mode=direct&page=1&page_size=100";

        while (nextUrl is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            request.Headers.TryAddWithoutValidation("Authorization", $"Token {credentials.ApiKey}");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ProviderSyncResult.Failed($"WebShare returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<WebShareProxyListResponse>(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("WebShare returned an empty proxy list response.");

            proxies.AddRange(payload.Results
                .Where(r => r.Valid)
                .Select(r => new ProviderProxyRecord(r.Id, r.ProxyAddress, r.Port, ProxyProtocol.Http, r.Username, r.Password,
                    IsActive: true, Country: r.CountryCode, ProviderGrouping: "Proxy List")));

            nextUrl = payload.Next;
        }

        return ProviderSyncResult.Ok(proxies);
    }

    public Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderRenewResult.Unsupported());
}
