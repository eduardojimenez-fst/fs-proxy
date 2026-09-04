using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.SetProxyTags;

/// <summary>
/// Full-replace tag assignment for one proxy — mirrors UpdateManualProxyCommandHandler's own
/// tag-diff logic exactly, generalized to every proxy (not just manual ones).
/// </summary>
public sealed class SetProxyTagsCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<SetProxyTagsCommand>
{
    public async ValueTask<Unit> Handle(SetProxyTagsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var proxy = await dbContext.Proxies.Include(x => x.TagAssignments)
            .FirstOrDefaultAsync(x => x.Id == command.ProxyId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Proxy {command.ProxyId} not found.");

        var newTagIds = await CreateManualProxyCommandHandler.ResolveTagIdsAsync(dbContext, command.TagNames, cancellationToken).ConfigureAwait(false);

        foreach (var tagId in proxy.TagAssignments.Select(a => a.TagId).Except(newTagIds).ToList())
        {
            proxy.UnassignTag(tagId);
        }
        foreach (var tagId in newTagIds)
        {
            proxy.AssignTag(tagId);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
