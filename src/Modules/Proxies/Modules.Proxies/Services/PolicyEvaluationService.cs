using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Services;

/// <summary>
/// Evaluates a proxy's recent negative <see cref="ProxyUsageEvent"/> history against whichever
/// <see cref="PolicyProfile"/> its tags resolve to (most-restrictive-wins when more than one tag
/// maps to a profile), and disables — or disables-and-renews — it once the profile's threshold
/// and distinct-reporter requirements are met. Called inline, immediately after any
/// <see cref="ProxyUsageEvent"/> is persisted (the health-check job and the consumer feedback
/// endpoint).
/// </summary>
public sealed class PolicyEvaluationService(ProxiesDbContext dbContext, IProxyRenewalService renewalService) : IPolicyEvaluationService
{
    public async Task EvaluateAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var proxy = await dbContext.Proxies.FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken).ConfigureAwait(false);
        if (proxy is null) return;

        var tagIds = await dbContext.Set<ProxyTagAssignment>().Where(a => a.ProxyId == proxyId).Select(a => a.TagId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (tagIds.Count == 0) return;

        // Most-restrictive-wins conflict rule from the spec: rank AutoDisableAndRenew(2) > AutoDisable(1) > Manual(0).
        var policy = await dbContext.Set<TagPolicyAssignment>()
            .Where(a => tagIds.Contains(a.TagId))
            .Join(dbContext.PolicyProfiles, a => a.PolicyProfileId, p => p.Id, (a, p) => p)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var resolved = policy.OrderByDescending(p => p.RestrictivenessRank).FirstOrDefault();
        if (resolved is null || resolved.Type == PolicyProfileType.Manual) return;

        var windowStart = DateTime.UtcNow.AddMinutes(-resolved.WindowMinutes);
        var negativeEvents = await dbContext.ProxyUsageEvents
            .Where(e => e.ProxyId == proxyId && e.OccurredAtUtc >= windowStart && e.Outcome != UsageEventOutcome.Success)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        int failureCount = negativeEvents.Count;
        int distinctReporters = negativeEvents
            .Select(e => e.Source == UsageEventSource.SystemHealthCheck ? "system" : e.ReportedByApiClientId?.ToString() ?? "unknown")
            .Distinct()
            .Count();

        if (failureCount < resolved.FailureThreshold || distinctReporters < resolved.MinDistinctReporters)
        {
            return;
        }

        proxy.SetStatus(ProxyStatus.Disabled);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (resolved.Type == PolicyProfileType.AutoDisableAndRenew)
        {
            await renewalService.TriggerAsync(proxyId, cancellationToken).ConfigureAwait(false);
        }
    }
}
