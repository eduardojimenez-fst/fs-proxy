using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Notifications.Data;
using FSH.Modules.Notifications.Domain;
using FSH.Modules.Notifications.Options;
using FSH.Modules.Proxies.Contracts.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Notifications.IntegrationEventHandlers;

/// <summary>
/// Subscribes to <see cref="ProviderAccountSyncFailedIntegrationEvent"/>, raised when a provider
/// account's consecutive sync failures cross the notification threshold. Same recipient-resolution
/// approach as <see cref="ManualProxyNeedsAttentionIntegrationEventHandler"/> — see
/// <see cref="ProxiesAlertOptions"/> for why.
/// </summary>
public sealed class ProviderAccountSyncFailedIntegrationEventHandler(
    NotificationsDbContext db,
    IOptions<ProxiesAlertOptions> options,
    ILogger<ProviderAccountSyncFailedIntegrationEventHandler> logger)
    : IIntegrationEventHandler<ProviderAccountSyncFailedIntegrationEvent>
{
    public async Task HandleAsync(ProviderAccountSyncFailedIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var adminUserId = options.Value.AdminUserId;
        if (string.IsNullOrWhiteSpace(adminUserId))
        {
            logger.LogWarning(
                "No ProxiesAlertOptions:AdminUserId configured — dropping provider-sync-failed notification for account {ProviderAccountId}.",
                @event.ProviderAccountId);
            return;
        }

        var notification = Notification.Create(
            userId: adminUserId,
            type: "proxies.provider-sync-failed",
            title: $"Provider account '{@event.ProviderAccountName}' sync is failing",
            body: $"{@event.ConsecutiveFailures} consecutive sync failures. Last error: {@event.LastErrorMessage ?? "unknown"}.",
            link: $"/proxies/provider-accounts/{@event.ProviderAccountId}",
            source: @event.Source,
            metadata: new { @event.ProviderAccountId, @event.ConsecutiveFailures });

        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogWarning("Recorded provider-sync-failed notification for account {ProviderAccountId}.", @event.ProviderAccountId);
    }
}
