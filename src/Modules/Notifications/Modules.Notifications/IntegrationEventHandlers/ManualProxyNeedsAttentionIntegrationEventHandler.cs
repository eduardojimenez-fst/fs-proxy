using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Notifications.Data;
using FSH.Modules.Notifications.Domain;
using FSH.Modules.Notifications.Options;
using FSH.Modules.Proxies.Contracts.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Notifications.IntegrationEventHandlers;

/// <summary>
/// Subscribes to <see cref="ManualProxyNeedsAttentionIntegrationEvent"/>, raised when a
/// <c>Manual</c>-type proxy is disabled by policy with no automated renewal path. The event
/// carries no tenant (every <c>Proxies</c> entity is global) and there is no "notify all admins"
/// broadcast mechanism in this module yet, so the recipient is the single admin user id
/// configured via <see cref="ProxiesAlertOptions.AdminUserId"/> — see that class for the
/// rationale. When it is unset, this is a graceful no-op: log and drop, not throw.
/// </summary>
public sealed class ManualProxyNeedsAttentionIntegrationEventHandler(
    NotificationsDbContext db,
    IOptions<ProxiesAlertOptions> options,
    ILogger<ManualProxyNeedsAttentionIntegrationEventHandler> logger)
    : IIntegrationEventHandler<ManualProxyNeedsAttentionIntegrationEvent>
{
    public async Task HandleAsync(ManualProxyNeedsAttentionIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var adminUserId = options.Value.AdminUserId;
        if (string.IsNullOrWhiteSpace(adminUserId))
        {
            logger.LogWarning(
                "No ProxiesAlertOptions:AdminUserId configured — dropping manual-proxy-needs-attention notification for proxy {ProxyId}.",
                @event.ProxyId);
            return;
        }

        var notification = Notification.Create(
            userId: adminUserId,
            type: "proxies.manual-needs-attention",
            title: "Manual proxy needs replacement",
            body: $"Proxy {@event.Host} was disabled by policy and has no automated renewal. Replace it manually.",
            link: $"/proxies?highlight={@event.ProxyId}",
            source: @event.Source,
            metadata: new { @event.ProxyId, @event.Host });

        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Recorded manual-proxy-needs-attention notification for proxy {ProxyId}.", @event.ProxyId);
        }
    }
}
