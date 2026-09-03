using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.DeleteApiClient;

public sealed class DeleteApiClientCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeleteApiClientCommand>
{
    public async ValueTask<Unit> Handle(DeleteApiClientCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var client = await dbContext.ApiClients.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"API client {command.Id} not found.");
        dbContext.ApiClients.Remove(client);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
