using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Tags;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Tags.ListTags;

public sealed class ListTagsQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<ListTagsQuery, IReadOnlyList<TagDto>>
{
    public async ValueTask<IReadOnlyList<TagDto>> Handle(ListTagsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var policyAssignments = await dbContext.Set<Domain.TagPolicyAssignment>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var targetAssignments = await dbContext.Set<Domain.TagHealthCheckTargetAssignment>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var policies = await dbContext.PolicyProfiles.AsNoTracking().ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false);
        var targets = await dbContext.HealthCheckTargets.AsNoTracking().ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false);

        var tags = await dbContext.Tags.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. tags.Select(tag =>
        {
            var policyId = policyAssignments.FirstOrDefault(a => a.TagId == tag.Id)?.PolicyProfileId;
            var targetId = targetAssignments.FirstOrDefault(a => a.TagId == tag.Id)?.HealthCheckTargetId;
            return new TagDto(
                tag.Id, tag.Name,
                policyId, policyId is { } pid ? policies.GetValueOrDefault(pid)?.Name : null,
                targetId, targetId is { } tid ? targets.GetValueOrDefault(tid)?.Name : null);
        })];
    }
}
