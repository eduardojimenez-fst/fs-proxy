using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.ListHealthCheckTargets;

public sealed class ListHealthCheckTargetsQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<ListHealthCheckTargetsQuery, IReadOnlyList<HealthCheckTargetDto>>
{
    public async ValueTask<IReadOnlyList<HealthCheckTargetDto>> Handle(ListHealthCheckTargetsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await dbContext.HealthCheckTargets.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new HealthCheckTargetDto(x.Id, x.Name, x.TestUrl, x.ExpectedStatusCode, x.ExpectedBodyKeyword, x.TimeoutMs))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
