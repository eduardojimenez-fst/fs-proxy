using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.ListApiClients;

public sealed class ListApiClientsQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<ListApiClientsQuery, IReadOnlyList<ApiClientDto>>
{
    public async ValueTask<IReadOnlyList<ApiClientDto>> Handle(ListApiClientsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await dbContext.ApiClients.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new ApiClientDto(x.Id, x.Name, x.IsEnabled, x.CreatedAtUtc, x.LastUsedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
