using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.UpdateManualProxy;

public sealed class UpdateManualProxyCommandHandler(
    ProxiesDbContext dbContext, [FromKeyedServices("proxy-password")] IProxySecretProtector proxyPasswordProtector)
    : ICommandHandler<UpdateManualProxyCommand>
{
    public async ValueTask<Unit> Handle(UpdateManualProxyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var proxy = await dbContext.Proxies.Include(x => x.TagAssignments)
            .FirstOrDefaultAsync(x => x.Id == command.Id && x.ProviderAccountId == ManualProviderAccount.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Manual proxy {command.Id} not found.");

        string? protectedPassword = string.IsNullOrWhiteSpace(command.PlaintextPassword)
            ? proxy.ProtectedPassword
            : proxyPasswordProtector.Protect(command.PlaintextPassword);

        var newTagIds = await CreateManualProxyCommandHandler.ResolveTagIdsAsync(dbContext, command.TagNames, cancellationToken).ConfigureAwait(false);
        foreach (var tagId in proxy.TagAssignments.Select(a => a.TagId).Except(newTagIds).ToList())
        {
            proxy.UnassignTag(tagId);
        }
        foreach (var tagId in newTagIds)
        {
            proxy.AssignTag(tagId);
        }

        proxy.UpdateConnection(command.Host, command.Port, command.Protocol, command.Username, protectedPassword);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
