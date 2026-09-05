using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;

namespace FSH.Modules.Proxies.Services;

public interface IProviderAccountSyncService
{
    Task<int> SyncAsync(Guid providerAccountId, CancellationToken cancellationToken);

    /// <summary>
    /// Upserts <paramref name="records"/> against <paramref name="account"/>'s existing proxies
    /// (matched by <see cref="Proxy.ExternalId"/>) and retires rows missing from
    /// <paramref name="records"/> — the single reconciliation algorithm shared by the live-adapter
    /// sync path (<see cref="SyncAsync"/>) and file-based import. Adds/updates entities on the
    /// tracked <c>ProxiesDbContext</c> but does not save — the caller calls
    /// <c>account.RecordSyncResult(...)</c> and <c>SaveChangesAsync</c> exactly once afterward, so
    /// both land in a single database round-trip.
    /// </summary>
    Task<(int Created, int Updated, int Retired)> ReconcileAsync(
        ProviderAccount account, IReadOnlyList<ProviderProxyRecord> records, CancellationToken cancellationToken);
}
