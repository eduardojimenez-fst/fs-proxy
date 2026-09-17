using FSH.Framework.Shared.Persistence;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.UsageEvents;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.UsageEvents.ListProxyUsageEvents;

public sealed class ListProxyUsageEventsQueryHandler(ProxiesDbContext dbContext)
    : IQueryHandler<ListProxyUsageEventsQuery, PagedResponse<ProxyUsageEventDto>>
{
    public async ValueTask<PagedResponse<ProxyUsageEventDto>> Handle(ListProxyUsageEventsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var q = dbContext.ProxyUsageEvents.AsNoTracking().AsQueryable();

        if (query.ProxyId is { } proxyId) q = q.Where(e => e.ProxyId == proxyId);
        if (query.Outcome is { } outcome) q = q.Where(e => e.Outcome == outcome);
        if (query.Source is { } source) q = q.Where(e => e.Source == source);
        // Mirrors the policy engine's definition of "negative" (PolicyEvaluationService).
        if (query.FailuresOnly) q = q.Where(e => e.Outcome != UsageEventOutcome.Success);
        if (query.FromUtc is { } from) q = q.Where(e => e.OccurredAtUtc >= from);
        if (query.ToUtc is { } to) q = q.Where(e => e.OccurredAtUtc <= to);

        long total = await q.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var page = await q
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id)
            .Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Resolve display names for the page only. Both lookups are left joins in effect: a
        // deleted API client or proxy must not drop its events from the timeline.
        var proxyIds = page.Select(e => e.ProxyId).Distinct().ToList();
        var proxies = await dbContext.Proxies.AsNoTracking()
            .Where(p => proxyIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Host, p.Port, p.Username })
            .ToDictionaryAsync(p => p.Id, p => (p.Host, p.Port, p.Username), cancellationToken).ConfigureAwait(false);

        var clientIds = page.Where(e => e.ReportedByApiClientId != null)
            .Select(e => e.ReportedByApiClientId!.Value).Distinct().ToList();
        var clientNames = clientIds.Count == 0
            ? []
            : await dbContext.ApiClients.AsNoTracking()
                .Where(c => clientIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken).ConfigureAwait(false);

        var items = page.Select(e =>
        {
            // A deleted proxy leaves its events behind (no cascade), so the lookup can miss: the
            // row still belongs in the timeline, just without connection details.
            proxies.TryGetValue(e.ProxyId, out var proxy);
            string? reporterName = e.ReportedByApiClientId is { } cid && clientNames.TryGetValue(cid, out string? n) ? n : null;

            return new ProxyUsageEventDto(
                e.Id, e.ProxyId, proxy.Host ?? string.Empty, proxy.Port, proxy.Username,
                e.Source, e.Outcome, e.ReportedByApiClientId, reporterName,
                e.HealthCheckTargetId, e.Detail, e.OccurredAtUtc);
        }).ToList();

        return new PagedResponse<ProxyUsageEventDto>
        {
            Items = items, PageNumber = query.PageNumber, PageSize = query.PageSize,
            TotalCount = total, TotalPages = (int)Math.Ceiling(total / (double)query.PageSize)
        };
    }
}
