using FSH.Modules.Proxies.Contracts;

namespace FSH.Modules.Proxies.Providers;

public interface IProxyProviderAdapterFactory
{
    IProxyProviderAdapter GetAdapter(ProxyProviderType providerType);
}
