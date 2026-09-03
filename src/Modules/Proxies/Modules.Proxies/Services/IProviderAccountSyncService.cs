namespace FSH.Modules.Proxies.Services;

public interface IProviderAccountSyncService
{
    Task<int> SyncAsync(Guid providerAccountId, CancellationToken cancellationToken);
}
