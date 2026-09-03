using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Authentication;

public sealed class ApiKeyAuthenticator(ProxiesDbContext dbContext, IApiKeyHasher hasher) : IApiKeyAuthenticator
{
    public async Task<Domain.ApiClient?> AuthenticateAsync(string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var hash = hasher.Hash(apiKey);
        var client = await dbContext.ApiClients.FirstOrDefaultAsync(c => c.ApiKeyHash == hash, cancellationToken).ConfigureAwait(false);
        if (client is null || !client.IsEnabled) return null;

        client.RecordUsage();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }
}
