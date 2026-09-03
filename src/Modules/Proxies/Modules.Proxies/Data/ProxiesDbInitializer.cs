using FSH.Framework.Persistence;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Proxies.Data;

public sealed class ProxiesDbInitializer(ProxiesDbContext dbContext, ILogger<ProxiesDbInitializer> logger)
    : IDbInitializer
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        if ((await dbContext.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
        {
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("[Proxies] applied migrations");
        }
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        bool exists = await dbContext.ProviderAccounts
            .AnyAsync(x => x.Id == ManualProviderAccount.Id, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        var manualAccount = ProviderAccount.CreateWithId(ManualProviderAccount.Id, "Manual", ProxyProviderType.Manual, "n/a");
        dbContext.ProviderAccounts.Add(manualAccount);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Seeded the well-known Manual provider account.");
    }
}
