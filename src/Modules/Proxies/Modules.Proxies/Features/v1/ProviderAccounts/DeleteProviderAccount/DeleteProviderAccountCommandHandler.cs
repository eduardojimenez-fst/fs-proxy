using System.Net;
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

        var proxies = await dbContext.Proxies.Where(x => x.ProviderAccountId == command.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (proxies.Count > 0)
        {
            if (!command.Force)
            {
                throw new CustomException(
                    $"Provider account \"{account.Name}\" has {proxies.Count} synced {(proxies.Count == 1 ? "proxy" : "proxies")} assigned. "
                    + "Retry with force=true to delete them along with the account.",
                    (IEnumerable<string>?)null,
                    HttpStatusCode.Conflict);
            }

            // Cascades to each proxy's ProxyTagAssignment/ProxyUsageEvent rows at the DB level
            // (both configured DeleteBehavior.Cascade on ProxyId).
            dbContext.Proxies.RemoveRange(proxies);
        }

        dbContext.ProviderAccounts.Remove(account);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
