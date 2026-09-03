using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Proxies.Services;

public sealed class HealthCheckTargetResolver(ProxiesDbContext dbContext, IOptions<ProxiesOptions> options) : IHealthCheckTargetResolver
{
    public async Task<IReadOnlyList<ResolvedHealthCheckTarget>> ResolveTargetsAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var tagIds = await dbContext.Set<ProxyTagAssignment>().Where(a => a.ProxyId == proxyId).Select(a => a.TagId).ToListAsync(cancellationToken).ConfigureAwait(false);

        var targets = await dbContext.Set<TagHealthCheckTargetAssignment>()
            .Where(a => tagIds.Contains(a.TagId))
            .Join(dbContext.HealthCheckTargets, a => a.HealthCheckTargetId, t => t.Id, (a, t) => t)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (targets.Count > 0)
        {
            return [.. targets.Select(t => new ResolvedHealthCheckTarget(t.Id, t.TestUrl, t.ExpectedStatusCode, t.ExpectedBodyKeyword, t.TimeoutMs))];
        }

        var defaults = options.Value;
        return [new ResolvedHealthCheckTarget(null, defaults.DefaultHealthCheckTargetUrl, null, null, defaults.DefaultHealthCheckTimeoutMs)];
    }
}
