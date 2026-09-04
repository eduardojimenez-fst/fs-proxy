using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.AssignProxyTag;

public sealed class AssignProxyTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<AssignProxyTagCommand, int>
{
    public async ValueTask<int> Handle(AssignProxyTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tagIds = await CreateManualProxyCommandHandler.ResolveTagIdsAsync(dbContext, [command.TagName], cancellationToken).ConfigureAwait(false);
        var tagId = tagIds[0];

        var proxies = await dbContext.Proxies.Include(x => x.TagAssignments)
            .Where(p => command.ProxyIds.Contains(p.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var proxy in proxies)
        {
            proxy.AssignTag(tagId);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return proxies.Count;
    }
}
