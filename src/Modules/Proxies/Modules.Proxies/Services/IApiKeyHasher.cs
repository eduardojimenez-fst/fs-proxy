namespace FSH.Modules.Proxies.Services;

public interface IApiKeyHasher
{
    string Hash(string plaintextKey);
    (string PlaintextKey, string Hash) GenerateKey();
}
