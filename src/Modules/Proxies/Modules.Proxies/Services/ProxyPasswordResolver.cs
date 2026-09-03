using FSH.Modules.Proxies.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.Proxies.Services;

/// <summary>
/// Picks the correct <see cref="IProxySecretProtector"/> for a <see cref="Proxy"/>'s password:
/// <c>ProxyPasswordProtector</c> ("proxy-password") for proxies attached to the well-known Manual
/// account, <c>ProviderAccountCredentialProtector</c> ("provider-account") for every other proxy.
///
/// Constructor parameters are typed as the shared <see cref="IProxySecretProtector"/> interface —
/// not the two concrete protector classes — and disambiguated via the keyed DI registrations
/// ProxiesModule already sets up in Task 8 (<c>"provider-account"</c>, <c>"proxy-password"</c>).
/// This mirrors the same testability pattern used by the ManualProxies CRUD handlers
/// (<c>[FromKeyedServices("proxy-password")] IProxySecretProtector</c>): it lets unit tests pass
/// small fakes for both protectors without needing real <c>IDataProtectionProvider</c> plumbing.
/// </summary>
public sealed class ProxyPasswordResolver(
    [FromKeyedServices("provider-account")] IProxySecretProtector providerProtector,
    [FromKeyedServices("proxy-password")] IProxySecretProtector manualProtector)
    : IProxyPasswordResolver
{
    public string? Decrypt(Proxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        if (proxy.ProtectedPassword is null) return null;
        IProxySecretProtector protector = proxy.ProviderAccountId == ManualProviderAccount.Id ? manualProtector : providerProtector;
        return protector.Unprotect(proxy.ProtectedPassword);
    }
}
