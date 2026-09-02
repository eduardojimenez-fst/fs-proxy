using FSH.Framework.Shared.Persistence;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.ListProviderAccounts;

public sealed class ListProviderAccountsQueryHandler(ProxiesDbContext dbContext)
    : IQueryHandler<ListProviderAccountsQuery, PagedResponse<ProviderAccountDto>>
{
    public async ValueTask<PagedResponse<ProviderAccountDto>> Handle(ListProviderAccountsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var q = dbContext.ProviderAccounts.AsNoTracking().OrderBy(x => x.Name);
        long total = await q.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await q.Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new ProviderAccountDto(x.Id, x.Name, x.ProviderType, x.IsEnabled, x.LastSyncedAtUtc, x.LastSyncStatus, x.ConsecutiveSyncFailures))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new PagedResponse<ProviderAccountDto>
        {
            Items = items,
            PageNumber = query.PageNumber,
            PageSize = query.PageSize,
            TotalCount = total,
            TotalPages = (int)Math.Ceiling(total / (double)query.PageSize)
        };
    }
}
