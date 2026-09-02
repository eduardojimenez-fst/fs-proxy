using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts;

namespace FSH.Modules.Proxies.Providers;

public sealed class ProxyProviderAdapterFactory(IEnumerable<IProxyProviderAdapter> adapters) : IProxyProviderAdapterFactory
{
    public IProxyProviderAdapter GetAdapter(ProxyProviderType providerType) =>
        adapters.FirstOrDefault(a => a.ProviderType == providerType)
        ?? throw new NotFoundException($"No provider adapter registered for '{providerType}'.");
}
