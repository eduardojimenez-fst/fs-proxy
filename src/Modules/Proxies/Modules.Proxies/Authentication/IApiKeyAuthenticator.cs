using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Authentication;

public interface IApiKeyAuthenticator
{
    Task<ApiClient?> AuthenticateAsync(string? apiKey, CancellationToken cancellationToken);
}
