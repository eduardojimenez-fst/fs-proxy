using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.SetProxiesStatus;

public sealed class SetProxiesStatusCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<SetProxiesStatusCommand, int>
{
    public async ValueTask<int> Handle(SetProxiesStatusCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        List<Proxy> targets;
        if (command.TagId is { } tagId)
        {
            var proxyIds = await dbContext.Set<ProxyTagAssignment>().Where(a => a.TagId == tagId).Select(a => a.ProxyId).ToListAsync(cancellationToken).ConfigureAwait(false);
            targets = await dbContext.Proxies.Where(p => proxyIds.Contains(p.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            targets = await dbContext.Proxies.Where(p => command.ProxyIds!.Contains(p.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var proxy in targets)
        {
            proxy.SetStatus(command.Status);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return targets.Count;
    }
}
