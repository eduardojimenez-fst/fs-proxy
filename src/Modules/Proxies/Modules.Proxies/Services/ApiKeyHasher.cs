using System.Security.Cryptography;
using System.Text;

namespace FSH.Modules.Proxies.Services;

public sealed class ApiKeyHasher : IApiKeyHasher
{
    private const int KeyBytesLength = 32;

    public string Hash(string plaintextKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintextKey);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintextKey));
        return Convert.ToHexStringLower(bytes);
    }

    public (string PlaintextKey, string Hash) GenerateKey()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(KeyBytesLength);
        var plaintextKey = $"fsh_proxies_{Convert.ToHexStringLower(randomBytes)}";
        return (plaintextKey, Hash(plaintextKey));
    }
}
