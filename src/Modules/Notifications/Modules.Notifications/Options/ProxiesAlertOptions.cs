namespace FSH.Modules.Notifications.Options;

/// <summary>
/// Recipient configuration for the <c>Proxies</c> module's admin-attention integration events
/// (<c>ManualProxyNeedsAttentionIntegrationEvent</c>, <c>ProviderAccountSyncFailedIntegrationEvent</c>).
///
/// These events carry no tenant (every <c>Proxies</c> entity is global) and there is no
/// broadcast-to-all-admins mechanism in <c>Notifications</c> today, so — rather than building
/// one now — the recipient is a single configured user id. A fresh install leaves this unset,
/// which is intentional: the handlers treat a missing <see cref="AdminUserId"/> as "no recipient
/// configured yet" and log + skip rather than fail. Swapping in a real permission-based broadcast
/// later only touches the two handlers' recipient-resolution logic, not this options class.
/// </summary>
public sealed class ProxiesAlertOptions
{
    public string? AdminUserId { get; set; }
}
