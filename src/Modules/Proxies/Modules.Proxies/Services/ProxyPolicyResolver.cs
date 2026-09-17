using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Services;

public sealed class ProxyPolicyResolver(ProxiesDbContext dbContext) : IProxyPolicyResolver
{
    public async Task<ResolvedProxyPolicy> ResolveAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var tagIds = await dbContext.Set<ProxyTagAssignment>()
            .Where(a => a.ProxyId == proxyId).Select(a => a.TagId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (tagIds.Count == 0)
        {
            return new ResolvedProxyPolicy(null, null, []);
        }

        var candidates = await dbContext.Set<TagPolicyAssignment>()
            .Where(a => tagIds.Contains(a.TagId))
            .Join(dbContext.PolicyProfiles, a => a.PolicyProfileId, p => p.Id, (a, p) => new { a.TagId, Profile = p })
            .Join(dbContext.Tags, x => x.TagId, t => t.Id, (x, t) => new PolicyCandidate(x.TagId, t.Name, x.Profile))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Most-restrictive-wins (spec conflict rule): AutoDisableAndRenew(2) > AutoDisable(1) > Manual(0).
        var winner = candidates.OrderByDescending(c => c.Profile.RestrictivenessRank).FirstOrDefault();
        return new ResolvedProxyPolicy(winner?.Profile, winner?.TagName, candidates);
    }
}
