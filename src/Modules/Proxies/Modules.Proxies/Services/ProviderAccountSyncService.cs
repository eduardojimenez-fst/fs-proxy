using FSH.Framework.Core.Exceptions;
using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Events;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Services;

/// <summary>
/// Reconciles a single <see cref="ProviderAccount"/> against its real provider: fetches the
/// current proxy list through the account's <see cref="IProxyProviderAdapter"/>, then upserts
/// matching rows (by <see cref="Proxy.ExternalId"/>) and retires rows the provider no longer
/// reports. This is the single place the reconciliation logic lives — both the sync-now command
/// handler and the hourly <see cref="Jobs.ProviderAccountSyncJob"/> call into it.
///
/// The <paramref name="protector"/> dependency is the unkeyed <c>IProxySecretProtector</c>
/// registered in <c>ProxiesModule.ConfigureServices</c> (Task 7), which resolves to
/// <see cref="ProviderAccountCredentialProtector"/> — the account-credential trust boundary,
/// not the manual-proxy one (<c>ProxyPasswordProtector</c>). Provider-sourced proxy passwords
/// travel through that same boundary since they originate from the account's own credentials.
/// Depending on the interface (rather than the concrete protector type) keeps this service
/// testable with a fake, matching the precedent set by the ProviderAccount CRUD handlers.
/// </summary>
public sealed class ProviderAccountSyncService(
    ProxiesDbContext dbContext, IProxyProviderAdapterFactory adapterFactory, IProxySecretProtector protector,
    IOutboxWriter outboxWriter)
    : IProviderAccountSyncService
{
    /// <summary>
    /// Consecutive-failure count at which a <see cref="ProviderAccountSyncFailedIntegrationEvent"/>
    /// is raised so an admin is notified (via <c>Modules.Notifications</c>) that the provider
    /// account's credentials or the provider itself needs attention.
    /// </summary>
    private const int SyncFailureNotificationThreshold = 3;

    public async Task<int> SyncAsync(Guid providerAccountId, CancellationToken cancellationToken)
    {
        var account = await dbContext.ProviderAccounts.FirstOrDefaultAsync(x => x.Id == providerAccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Provider account {providerAccountId} not found.");

        var adapter = adapterFactory.GetAdapter(account.ProviderType);
        if (!adapter.SupportsSync)
        {
            return 0;
        }

        var decrypted = protector.Unprotect(account.ProtectedCredentials);
        var result = await adapter.SyncProxiesAsync(account, decrypted, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            account.RecordSyncResult(success: false, statusMessage: result.ErrorMessage);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (account.ConsecutiveSyncFailures >= SyncFailureNotificationThreshold)
            {
                await outboxWriter.AddAsync(
                    new ProviderAccountSyncFailedIntegrationEvent(
                        Guid.CreateVersion7(), DateTime.UtcNow, TenantId: null, Guid.NewGuid().ToString(), "Proxies",
                        account.Id, account.Name, account.ConsecutiveSyncFailures, result.ErrorMessage),
                    cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }

        var (created, updated, retired) = await ReconcileAsync(account, result.Proxies, cancellationToken).ConfigureAwait(false);

        account.RecordSyncResult(success: true, statusMessage: $"Synced {result.Proxies.Count} proxies.");
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return created + updated + retired;
    }

    public async Task<(int Created, int Updated, int Retired)> ReconcileAsync(
        ProviderAccount account, IReadOnlyList<ProviderProxyRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(records);

        var existingProxies = await dbContext.Proxies
            .Where(p => p.ProviderAccountId == account.Id && p.ExternalId != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byExternalId = existingProxies.ToDictionary(p => p.ExternalId!);
        var incomingExternalIds = records.Select(p => p.ExternalId).ToHashSet();

        int created = 0, updated = 0;
        foreach (var record in records)
        {
            if (byExternalId.TryGetValue(record.ExternalId, out var existing))
            {
                existing.UpdateConnection(record.Host, record.Port, record.Protocol, record.Username,
                    record.Password is null ? null : protector.Protect(record.Password),
                    record.Geolocation, record.ProviderGrouping, record.Kind);
                updated++;
            }
            else
            {
                var newProxy = Proxy.Create(account.Id, record.Host, record.Port, record.Protocol, record.Username,
                    record.Password is null ? null : protector.Protect(record.Password), record.ExternalId,
                    record.Geolocation, record.ProviderGrouping, record.Kind);
                dbContext.Proxies.Add(newProxy);
                created++;
            }
        }

        int retired = 0;
        foreach (var stale in existingProxies.Where(p => !incomingExternalIds.Contains(p.ExternalId!) && p.Status != ProxyStatus.Retired))
        {
            stale.SetStatus(ProxyStatus.Retired);
            retired++;
        }

        return (created, updated, retired);
    }
}
