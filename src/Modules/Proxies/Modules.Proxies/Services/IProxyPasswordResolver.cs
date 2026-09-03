using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Services;

public interface IProxyPasswordResolver
{
    string? Decrypt(Proxy proxy);
}
