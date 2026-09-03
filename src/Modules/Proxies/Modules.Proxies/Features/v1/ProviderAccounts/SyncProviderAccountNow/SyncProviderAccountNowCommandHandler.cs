using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Services;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountNow;

public sealed class SyncProviderAccountNowCommandHandler(IProviderAccountSyncService syncService)
    : ICommandHandler<SyncProviderAccountNowCommand, int>
{
    public async ValueTask<int> Handle(SyncProviderAccountNowCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await syncService.SyncAsync(command.ProviderAccountId, cancellationToken).ConfigureAwait(false);
    }
}
