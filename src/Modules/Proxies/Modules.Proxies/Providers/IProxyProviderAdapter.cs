using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers;

public interface IProxyProviderAdapter
{
    ProxyProviderType ProviderType { get; }
    bool SupportsSync { get; }
    bool SupportsRenew { get; }

    Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken);

    Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken);
}
