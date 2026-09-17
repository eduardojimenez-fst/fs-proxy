using FSH.Framework.Shared.Persistence;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ListProxies;

public sealed class ListProxiesQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<ListProxiesQuery, PagedResponse<ProxyDto>>
{
    public async ValueTask<PagedResponse<ProxyDto>> Handle(ListProxiesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var q = dbContext.Proxies.AsNoTracking().AsQueryable();

        if (query.Status is { } status) q = q.Where(p => p.Status == status);
        if (query.ProviderAccountId is { } accountId) q = q.Where(p => p.ProviderAccountId == accountId);
        if (!string.IsNullOrWhiteSpace(query.Geolocation))
        {
            var normalizedGeolocation = query.Geolocation.ToUpperInvariant();
#pragma warning disable CA1862, CA1304, CA1311 // EF Core cannot translate string.Equals(..., StringComparison) to SQL; using ToUpper() is the translatable approach — see https://learn.microsoft.com/ef/core/miscellaneous/collations-and-case-sensitivity
            q = q.Where(p => p.Geolocation != null && p.Geolocation.ToUpper() == normalizedGeolocation);
#pragma warning restore CA1862, CA1304, CA1311
        }
        if (query.Kind is { } kind) q = q.Where(p => p.Kind == kind);
        // Host/Username are free-text "contains" searches. PostgreSQL is case-sensitive by
        // default (SQL Server is not), so normalize both sides the same way the geolocation
        // filter above does — see the CA1862 note there.
        if (!string.IsNullOrWhiteSpace(query.Host))
        {
            var normalizedHost = query.Host.Trim().ToUpperInvariant();
#pragma warning disable CA1862, CA1304, CA1311
            q = q.Where(p => p.Host.ToUpper().Contains(normalizedHost));
#pragma warning restore CA1862, CA1304, CA1311
        }
        if (!string.IsNullOrWhiteSpace(query.Username))
        {
            var normalizedUsername = query.Username.Trim().ToUpperInvariant();
#pragma warning disable CA1862, CA1304, CA1311
            q = q.Where(p => p.Username != null && p.Username.ToUpper().Contains(normalizedUsername));
#pragma warning restore CA1862, CA1304, CA1311
        }
        if (query.Tags is { Count: > 0 })
        {
            var normalized = query.Tags.Select(Tag.Normalize).ToList();
            var matchingTagIds = await dbContext.Tags.Where(t => normalized.Contains(t.Name)).Select(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var proxyIdsWithAnyTag = dbContext.Set<ProxyTagAssignment>().Where(a => matchingTagIds.Contains(a.TagId)).Select(a => a.ProxyId);
            q = q.Where(p => proxyIdsWithAnyTag.Contains(p.Id));
        }

        long total = await q.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var page = await q.OrderBy(p => p.Host).Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var accountNames = await dbContext.ProviderAccounts.AsNoTracking()
            .Where(a => page.Select(p => p.ProviderAccountId).Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => (a.Name, a.ProviderType), cancellationToken).ConfigureAwait(false);
        var proxyIdsOnPage = page.Select(p => p.Id).ToList();
        var tagsByProxy = await dbContext.Set<ProxyTagAssignment>().AsNoTracking()
            .Where(a => proxyIdsOnPage.Contains(a.ProxyId))
            .Join(dbContext.Tags.AsNoTracking(), a => a.TagId, t => t.Id, (a, t) => new { a.ProxyId, t.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // One indexed aggregate over the page's proxies (IX_ProxyUsageEvents_ProxyId_OccurredAtUtc),
        // counted in SQL rather than pulled into memory — a busy proxy can have thousands of events
        // a day once consumers are reporting feedback.
        var since = DateTime.UtcNow.AddHours(-24);
        var health = await dbContext.ProxyUsageEvents.AsNoTracking()
            .Where(e => proxyIdsOnPage.Contains(e.ProxyId) && e.OccurredAtUtc >= since)
            .GroupBy(e => e.ProxyId)
            .Select(g => new
            {
                ProxyId = g.Key,
                Success = g.Count(e => e.Outcome == UsageEventOutcome.Success),
                Failure = g.Count(e => e.Outcome != UsageEventOutcome.Success),
                LastAt = g.Max(e => e.OccurredAtUtc)
            })
            .ToDictionaryAsync(x => x.ProxyId, cancellationToken).ConfigureAwait(false);

        var items = page.Select(p =>
        {
            health.TryGetValue(p.Id, out var h);
            return new ProxyDto(
                p.Id, p.Host, p.Port, p.Protocol, p.Status,
                p.ProviderAccountId, accountNames[p.ProviderAccountId].Name, accountNames[p.ProviderAccountId].ProviderType,
                tagsByProxy.Where(t => t.ProxyId == p.Id).Select(t => t.Name).ToList(),
                p.CreatedAtUtc, p.LastRenewedAtUtc, p.Geolocation, p.ProviderGrouping, p.Kind, p.Username,
                h?.Success ?? 0, h?.Failure ?? 0, h?.LastAt);
        }).ToList();

        return new PagedResponse<ProxyDto>
        {
            Items = items, PageNumber = query.PageNumber, PageSize = query.PageSize,
            TotalCount = total, TotalPages = (int)Math.Ceiling(total / (double)query.PageSize)
        };
    }
}
