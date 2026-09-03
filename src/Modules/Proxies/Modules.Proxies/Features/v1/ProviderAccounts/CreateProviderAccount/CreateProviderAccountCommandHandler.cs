using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.CreateProviderAccount;

public sealed class CreateProviderAccountCommandHandler(
    ProxiesDbContext dbContext, IProxySecretProtector protector)
    : ICommandHandler<CreateProviderAccountCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateProviderAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = ProviderAccount.Create(command.Name, command.ProviderType, protector.Protect(command.PlaintextCredentials));
        dbContext.ProviderAccounts.Add(account);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return account.Id;
    }
}
