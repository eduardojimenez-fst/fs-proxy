using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Policies.ListPolicyProfiles;

public sealed class ListPolicyProfilesQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<ListPolicyProfilesQuery, IReadOnlyList<PolicyProfileDto>>
{
    public async ValueTask<IReadOnlyList<PolicyProfileDto>> Handle(ListPolicyProfilesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await dbContext.PolicyProfiles.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new PolicyProfileDto(x.Id, x.Name, x.Type, x.FailureThreshold, x.WindowMinutes, x.MinDistinctReporters))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
