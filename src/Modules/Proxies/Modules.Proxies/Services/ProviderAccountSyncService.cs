using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts;
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
    ProxiesDbContext dbContext, IProxyProviderAdapterFactory adapterFactory, IProxySecretProtector protector)
    : IProviderAccountSyncService
{
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
            return 0;
        }

        var existingProxies = await dbContext.Proxies
            .Where(p => p.ProviderAccountId == providerAccountId && p.ExternalId != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byExternalId = existingProxies.ToDictionary(p => p.ExternalId!);
        var incomingExternalIds = result.Proxies.Select(p => p.ExternalId).ToHashSet();

        int touched = 0;
        foreach (var record in result.Proxies)
        {
            if (byExternalId.TryGetValue(record.ExternalId, out var existing))
            {
                existing.UpdateConnection(record.Host, record.Port, record.Protocol, record.Username, record.Password is null ? null : protector.Protect(record.Password));
            }
            else
            {
                var created = Proxy.Create(providerAccountId, record.Host, record.Port, record.Protocol, record.Username,
                    record.Password is null ? null : protector.Protect(record.Password), record.ExternalId);
                dbContext.Proxies.Add(created);
            }

            touched++;
        }

        foreach (var stale in existingProxies.Where(p => !incomingExternalIds.Contains(p.ExternalId!) && p.Status != ProxyStatus.Retired))
        {
            stale.SetStatus(ProxyStatus.Retired);
            touched++;
        }

        account.RecordSyncResult(success: true, statusMessage: $"Synced {result.Proxies.Count} proxies.");
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return touched;
    }
}
