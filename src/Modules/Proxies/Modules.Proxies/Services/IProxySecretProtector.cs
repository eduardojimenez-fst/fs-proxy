namespace FSH.Modules.Proxies.Services;

public interface IProxySecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}
