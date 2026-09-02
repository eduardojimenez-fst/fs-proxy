using FSH.Framework.Shared.Persistence;
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

        var items = page.Select(p => new ProxyDto(
            p.Id, p.Host, p.Port, p.Protocol, p.Status,
            p.ProviderAccountId, accountNames[p.ProviderAccountId].Name, accountNames[p.ProviderAccountId].ProviderType,
            tagsByProxy.Where(t => t.ProxyId == p.Id).Select(t => t.Name).ToList(),
            p.CreatedAtUtc, p.LastRenewedAtUtc)).ToList();

        return new PagedResponse<ProxyDto>
        {
            Items = items, PageNumber = query.PageNumber, PageSize = query.PageSize,
            TotalCount = total, TotalPages = (int)Math.Ceiling(total / (double)query.PageSize)
        };
    }
}
