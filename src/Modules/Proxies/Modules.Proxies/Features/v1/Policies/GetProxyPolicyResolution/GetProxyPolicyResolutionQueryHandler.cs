using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Policies.GetProxyPolicyResolution;

public sealed class GetProxyPolicyResolutionQueryHandler(
    ProxiesDbContext dbContext, IProxyPolicyResolver policyResolver, IHealthCheckTargetResolver targetResolver)
    : IQueryHandler<GetProxyPolicyResolutionQuery, ProxyPolicyResolutionDto>
{
    public async ValueTask<ProxyPolicyResolutionDto> Handle(GetProxyPolicyResolutionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        bool exists = await dbContext.Proxies.AnyAsync(p => p.Id == query.ProxyId, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            throw new NotFoundException($"Proxy {query.ProxyId} not found.");
        }

        var proxyTags = await dbContext.Set<ProxyTagAssignment>()
            .Where(a => a.ProxyId == query.ProxyId)
            .Join(dbContext.Tags, a => a.TagId, t => t.Id, (a, t) => new { t.Id, t.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var tagNames = proxyTags.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var proxyTagIds = proxyTags.Select(t => t.Id).ToList();

        // Same resolver the engine calls, so this can never explain a decision it wouldn't make.
        var resolved = await policyResolver.ResolveAsync(query.ProxyId, cancellationToken).ConfigureAwait(false);

        int? failures = null;
        int? reporters = null;
        DateTime? windowStart = null;
        if (resolved.Winner is { } winner)
        {
            windowStart = DateTime.UtcNow.AddMinutes(-winner.WindowMinutes);
            // Mirrors PolicyEvaluationService's counting: non-success events only, and every health
            // check counts as the single reporter "system".
            var negative = await dbContext.ProxyUsageEvents.AsNoTracking()
                .Where(e => e.ProxyId == query.ProxyId && e.OccurredAtUtc >= windowStart && e.Outcome != UsageEventOutcome.Success)
                .Select(e => new { e.Source, e.ReportedByApiClientId })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            failures = negative.Count;
            reporters = negative
                .Select(e => e.Source == UsageEventSource.SystemHealthCheck ? "system" : e.ReportedByApiClientId?.ToString() ?? "unknown")
                .Distinct().Count();
        }

        var targets = await targetResolver.ResolveTargetsAsync(query.ProxyId, cancellationToken).ConfigureAwait(false);
        bool usingDefault = targets.Count > 0 && targets.All(t => t.TargetId is null);

        var targetIds = targets.Where(t => t.TargetId is not null).Select(t => t.TargetId!.Value).ToList();
        var targetNames = targetIds.Count == 0
            ? []
            : await dbContext.HealthCheckTargets.AsNoTracking()
                .Where(t => targetIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken).ConfigureAwait(false);

        // Which of THIS proxy's tags brought each target in — the assignment is per tag, and the
        // same target can be assigned to tags the proxy does not carry.
        var targetTags = targetIds.Count == 0
            ? []
            : await dbContext.Set<TagHealthCheckTargetAssignment>()
                .Where(a => targetIds.Contains(a.HealthCheckTargetId) && proxyTagIds.Contains(a.TagId))
                .Join(dbContext.Tags, a => a.TagId, t => t.Id, (a, t) => new { a.HealthCheckTargetId, TagName = t.Name })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

        var targetDtos = targets.Select(t => new ResolvedHealthCheckTargetDto(
            t.TargetId,
            t.TargetId is { } id && targetNames.TryGetValue(id, out string? n) ? n : "Default (configured fallback)",
            t.TestUrl, t.ExpectedStatusCode, t.ExpectedBodyKeyword, t.TimeoutMs,
            t.TargetId is { } tid ? targetTags.FirstOrDefault(x => x.HealthCheckTargetId == tid)?.TagName : null)).ToList();

        var candidates = resolved.Candidates
            .Select(c => new PolicyCandidateDto(
                c.TagId, c.TagName, ToDto(c.Profile), ReferenceEquals(c.Profile, resolved.Winner)))
            .ToList();

        return new ProxyPolicyResolutionDto(
            query.ProxyId, tagNames,
            resolved.Winner is null ? null : ToDto(resolved.Winner), resolved.WinnerTagName,
            candidates, failures, reporters, windowStart, targetDtos, usingDefault);
    }

    private static PolicyProfileDto ToDto(PolicyProfile p) =>
        new(p.Id, p.Name, p.Type, p.FailureThreshold, p.WindowMinutes, p.MinDistinctReporters);
}
