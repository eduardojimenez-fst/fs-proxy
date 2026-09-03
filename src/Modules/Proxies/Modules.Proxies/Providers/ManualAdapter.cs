using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers;

/// <summary>
/// Self-hosted proxies have no provider API — sync is a no-op (rows are managed directly
/// through the Manual Proxy admin CRUD, Task 8) and renewal always reports unsupported so
/// the caller falls back to the admin-notification flow (Task 19).
/// </summary>
public sealed class ManualAdapter : IProxyProviderAdapter
{
    public ProxyProviderType ProviderType => ProxyProviderType.Manual;
    public bool SupportsSync => false;
    public bool SupportsRenew => false;

    public Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderSyncResult.Ok([]));

    public Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderRenewResult.Unsupported());
}
