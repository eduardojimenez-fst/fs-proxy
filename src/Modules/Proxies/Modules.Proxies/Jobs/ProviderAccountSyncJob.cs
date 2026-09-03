using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Proxies.Jobs;

/// <summary>
/// Hourly reconciliation of every enabled <see cref="Domain.ProviderAccount"/> against its real
/// provider. One account's failure must not abort the sync of the rest — mirrors
/// WebhookFanoutHandler's "one enqueue throws must not abort fan-out to the rest" resilience
/// pattern.
/// </summary>
public sealed class ProviderAccountSyncJob(ProxiesDbContext dbContext, IProviderAccountSyncService syncService, ILogger<ProviderAccountSyncJob> logger)
{
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [60, 300])]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var enabledAccountIds = await dbContext.ProviderAccounts
            .Where(a => a.IsEnabled)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var accountId in enabledAccountIds)
        {
            try
            {
                int touched = await syncService.SyncAsync(accountId, cancellationToken).ConfigureAwait(false);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Synced provider account {ProviderAccountId}: {Touched} proxies touched.", accountId, touched);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Provider account sync failed for {ProviderAccountId}.", accountId);
            }
        }
    }
}
