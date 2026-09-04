using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.UnassignProxyTag;

public sealed class UnassignProxyTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<UnassignProxyTagCommand, int>
{
    public async ValueTask<int> Handle(UnassignProxyTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalized = Tag.Normalize(command.TagName);
        var tag = await dbContext.Tags.FirstOrDefaultAsync(t => t.Name == normalized, cancellationToken).ConfigureAwait(false);
        if (tag is null)
        {
            return 0;
        }

        var proxies = await dbContext.Proxies.Include(x => x.TagAssignments)
            .Where(p => command.ProxyIds.Contains(p.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        int touched = 0;
        foreach (var proxy in proxies)
        {
            if (proxy.TagAssignments.Any(a => a.TagId == tag.Id))
            {
                proxy.UnassignTag(tag.Id);
                touched++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return touched;
    }
}
