using FSH.Framework.Eventing.Abstractions;

namespace FSH.Modules.Proxies.Contracts.Events;

/// <summary>
/// Raised when a <c>Manual</c>-type proxy is disabled by policy and has no automated renewal
/// path, so an admin must replace it by hand. <c>TenantId</c> is always <see langword="null"/> —
/// every <c>Proxies</c> entity is <c>IGlobalEntity</c>, so there is no tenant to stamp.
/// Consumed by <c>Modules.Notifications</c>.
/// </summary>
public sealed record ManualProxyNeedsAttentionIntegrationEvent(
    Guid Id, DateTime OccurredOnUtc, string? TenantId, string CorrelationId, string Source,
    Guid ProxyId, string Host) : IIntegrationEvent;
