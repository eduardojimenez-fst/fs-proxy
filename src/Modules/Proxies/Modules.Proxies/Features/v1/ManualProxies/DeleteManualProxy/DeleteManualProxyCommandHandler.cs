using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.DeleteManualProxy;

public sealed class DeleteManualProxyCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeleteManualProxyCommand>
{
    public async ValueTask<Unit> Handle(DeleteManualProxyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var proxy = await dbContext.Proxies
            .FirstOrDefaultAsync(x => x.Id == command.Id && x.ProviderAccountId == ManualProviderAccount.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Manual proxy {command.Id} not found.");

        dbContext.Proxies.Remove(proxy);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
