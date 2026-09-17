using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Services;

/// <summary>
/// Evaluates a proxy's recent negative <see cref="ProxyUsageEvent"/> history against whichever
/// <see cref="PolicyProfile"/> its tags resolve to (most-restrictive-wins when more than one tag
/// maps to a profile), and disables — or disables-and-renews — it once the profile's threshold
/// and distinct-reporter requirements are met. Called inline, immediately after a
/// <see cref="ProxyUsageEvent"/> is persisted, by three callers: the health-check job, the
/// single-event consumer feedback endpoint, and the batched consumer feedback endpoint.
///
/// This service only counts events where <c>Outcome != Success</c> (see its own
/// <c>negativeEvents</c> query below) — a proxy with nothing but successful events can never trip
/// the threshold. The batch handler relies on that as a semantic guarantee: it deliberately skips
/// calling <see cref="EvaluateAsync"/> for any proxy whose batch contained only <c>Success</c>
/// outcomes, on the assumption that doing so is provably a no-op. If <see cref="EvaluateAsync"/>
/// is ever changed to become sensitive to <c>Success</c> events, that skip in the batch handler
/// will silently stop evaluating proxies it should evaluate.
/// </summary>
public sealed class PolicyEvaluationService(
    ProxiesDbContext dbContext, IProxyRenewalService renewalService, IProxyPolicyResolver policyResolver)
    : IPolicyEvaluationService
{
    public async Task EvaluateAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var proxy = await dbContext.Proxies.FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken).ConfigureAwait(false);
        if (proxy is null) return;

        // Idempotency guard. Only an Active proxy is a candidate for the policy to act on:
        //  - Disabled/Banned/Retired means the policy (or an admin) has already acted, and a burst
        //    of concurrent feedback reports for the same banned proxy must not re-run
        //    SetStatus(Disabled) + IProxyRenewalService.TriggerAsync N times, each of which can
        //    publish its own ManualProxyNeedsAttentionIntegrationEvent (notification storm).
        //  - Testing means the proxy is not being served yet (RequestProxies only returns Active)
        //    and is awaiting promotion by the active health check, so there is nothing to disable.
        //    This also closes the renew loop: a successful renewal calls Proxy.MarkRenewed(), which
        //    puts the proxy back in Testing while the old failure events are still inside the
        //    policy window — without this guard the very next event would disable-and-renew again.
        if (proxy.Status != ProxyStatus.Active) return;

        // Resolution (tags → profile, most-restrictive-wins) lives in IProxyPolicyResolver so the
        // admin UI can explain the same decision this method acts on. See ProxyPolicyResolver.
        var resolved = (await policyResolver.ResolveAsync(proxyId, cancellationToken).ConfigureAwait(false)).Winner;
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
