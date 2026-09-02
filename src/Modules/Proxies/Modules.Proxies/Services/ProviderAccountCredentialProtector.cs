using Microsoft.AspNetCore.DataProtection;

namespace FSH.Modules.Proxies.Services;

/// <summary>
/// Encrypts/decrypts ProviderAccount API credentials at rest. Distinct purpose string from
/// ProxyPasswordProtector and from Webhooks' own protector — Data Protection purpose strings
/// are how different secret categories stay cryptographically isolated from each other.
/// </summary>
public sealed class ProviderAccountCredentialProtector : IProxySecretProtector
{
    private readonly IDataProtector _protector;

    public ProviderAccountCredentialProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector("FSH.Proxies.ProviderCredential.v1");
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
