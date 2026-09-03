using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.DeleteProviderAccount;

public sealed class DeleteProviderAccountCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeleteProviderAccountCommand>
{
    public async ValueTask<Unit> Handle(DeleteProviderAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = await dbContext.ProviderAccounts.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Provider account {command.Id} not found.");

        dbContext.ProviderAccounts.Remove(account);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
