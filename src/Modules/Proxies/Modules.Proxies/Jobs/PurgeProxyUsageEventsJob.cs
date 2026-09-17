using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Options;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Proxies.Jobs;

/// <summary>
/// Daily purge of ProxyUsageEvent rows older than <see cref="ProxiesOptions.UsageEventRetentionDays"/>.
///
/// The table is append-only and grows without bound: the active health check writes one row per
/// proxy per target every 15 minutes, and every consumer feedback report adds another. Nothing
/// reads events older than the widest policy window except the operator timeline, so old rows are
/// pure cost.
///
/// Deleting in bounded batches keeps each statement's transaction and lock footprint small — a
/// single unbounded DELETE over months of accumulated rows can block the feedback endpoints for
/// its whole duration.
/// </summary>
public sealed partial class PurgeProxyUsageEventsJob(
    ProxiesDbContext dbContext,
    IOptions<ProxiesOptions> options,
    ILogger<PurgeProxyUsageEventsJob> logger)
{
    /// <summary>Rows removed per statement. Bounded so one run never takes a long-lived lock.</summary>
    private const int BatchSize = 5000;

    /// <summary>At most 200k rows per run, so a first run over a large backlog is spread across days.</summary>
    private const int MaxBatchesPerRun = 40;

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 300, 900])]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        int retentionDays = options.Value.UsageEventRetentionDays;
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        int totalDeleted = 0;
        for (int batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Pick the batch by id first: ExecuteDelete does not accept Take() on every provider,
            // and this app ships both SQL Server and PostgreSQL. Ordering by the indexed
            // OccurredAtUtc (IX_ProxyUsageEvents_ProxyId_OccurredAtUtc) takes the oldest rows first.
            var ids = await dbContext.ProxyUsageEvents.AsNoTracking()
                .Where(e => e.OccurredAtUtc < cutoff)
                .OrderBy(e => e.OccurredAtUtc)
                .Take(BatchSize)
                .Select(e => e.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (ids.Count == 0)
            {
                break;
            }

            totalDeleted += await dbContext.ProxyUsageEvents
                .Where(e => ids.Contains(e.Id))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            if (ids.Count < BatchSize)
            {
                break;
            }
        }

        if (totalDeleted > 0)
        {
            LogPurged(logger, totalDeleted, retentionDays, cutoff);
        }
    }

    [LoggerMessage(
        EventId = 5100,
        Level = LogLevel.Information,
        Message = "Purged {DeletedCount} proxy usage events older than {RetentionDays} days (cutoff {CutoffUtc}).")]
    private static partial void LogPurged(ILogger logger, int deletedCount, int retentionDays, DateTime cutoffUtc);
}
