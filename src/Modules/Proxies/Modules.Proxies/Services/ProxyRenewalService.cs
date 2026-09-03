using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Proxies.Contracts.Events;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Providers;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Services;

/// <summary>
/// Handles both the "no automated renewal exists" case (Manual proxies, or any adapter that
/// doesn't support it) and the "renewal was attempted but failed" case identically — in both,
/// an admin needs to look at the proxy by hand, so both raise the same
/// <see cref="ManualProxyNeedsAttentionIntegrationEvent"/> regardless of which provider it came
/// from. A proxy whose <c>ProviderAccountId</c> doesn't resolve to any row (a data-integrity
/// issue outside normal flow, mirroring the <c>proxy is null</c> guard above it) is a silent
/// no-op rather than a needs-attention publish — there is no host/provider context to act on.
///
/// The <paramref name="protector"/> dependency is the unkeyed <see cref="IProxySecretProtector"/>
/// registered in <c>ProxiesModule.ConfigureServices</c> (Task 7), which resolves to
/// <see cref="ProviderAccountCredentialProtector"/> — the account-credential trust boundary,
/// matching <see cref="ProviderAccountSyncService"/>'s precedent. Depending on the interface
/// (rather than the concrete protector type) keeps this service testable with a fake.
/// </summary>
public sealed class ProxyRenewalService(
    ProxiesDbContext dbContext, IProxyProviderAdapterFactory adapterFactory,
    IProxySecretProtector protector, IOutboxWriter outboxWriter)
    : IProxyRenewalService
{
    public async Task TriggerAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var proxy = await dbContext.Proxies.FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken).ConfigureAwait(false);
        if (proxy is null) return;
        var account = await dbContext.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == proxy.ProviderAccountId, cancellationToken).ConfigureAwait(false);
        if (account is null) return;

        var adapter = adapterFactory.GetAdapter(account.ProviderType);

        if (!adapter.SupportsRenew)
        {
            await PublishNeedsAttentionAsync(proxy.Id, proxy.Host, cancellationToken).ConfigureAwait(false);
            return;
        }

        var decrypted = protector.Unprotect(account.ProtectedCredentials);
        var result = await adapter.RenewProxyAsync(account, decrypted, proxy, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            await PublishNeedsAttentionAsync(proxy.Id, proxy.Host, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result.UpdatedProxy is { } updated)
        {
            proxy.UpdateConnection(updated.Host, updated.Port, updated.Protocol, updated.Username,
                updated.Password is null ? null : protector.Protect(updated.Password));
        }
        proxy.MarkRenewed();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishNeedsAttentionAsync(Guid proxyId, string host, CancellationToken cancellationToken) =>
        await outboxWriter.AddAsync(
            new ManualProxyNeedsAttentionIntegrationEvent(Guid.CreateVersion7(), DateTime.UtcNow, TenantId: null, Guid.NewGuid().ToString(), "Proxies", proxyId, host),
            cancellationToken).ConfigureAwait(false);
}
