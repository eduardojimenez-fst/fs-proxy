using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.UpdateProviderAccount;

public sealed class UpdateProviderAccountCommandHandler(
    ProxiesDbContext dbContext, IProxySecretProtector protector)
    : ICommandHandler<UpdateProviderAccountCommand>
{
    public async ValueTask<Unit> Handle(UpdateProviderAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = await dbContext.ProviderAccounts.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Provider account {command.Id} not found.");

        account.Rename(command.Name);
        account.SetEnabled(command.IsEnabled);
        if (!string.IsNullOrWhiteSpace(command.PlaintextCredentials))
        {
            account.UpdateCredentials(protector.Protect(command.PlaintextCredentials));
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
