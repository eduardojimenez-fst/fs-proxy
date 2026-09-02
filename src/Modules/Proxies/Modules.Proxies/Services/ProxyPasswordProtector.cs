using Microsoft.AspNetCore.DataProtection;

namespace FSH.Modules.Proxies.Services;

public sealed class ProxyPasswordProtector : IProxySecretProtector
{
    private readonly IDataProtector _protector;

    public ProxyPasswordProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector("FSH.Proxies.ProxyPassword.v1");
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
