using FSH.Framework.Eventing.Abstractions;

namespace FSH.Modules.Proxies.Contracts.Events;

/// <summary>
/// Raised by <c>ProviderAccountSyncService</c> when a <see cref="ProviderAccountId"/>'s
/// consecutive sync-failure count crosses the notification threshold, so an admin can
/// investigate the provider credential/outage. <c>TenantId</c> is always <see langword="null"/> —
/// every <c>Proxies</c> entity is <c>IGlobalEntity</c>, so there is no tenant to stamp. Consumed
/// by <c>Modules.Notifications</c>.
/// </summary>
public sealed record ProviderAccountSyncFailedIntegrationEvent(
    Guid Id, DateTime OccurredOnUtc, string? TenantId, string CorrelationId, string Source,
    Guid ProviderAccountId, string ProviderAccountName, int ConsecutiveFailures, string? LastErrorMessage) : IIntegrationEvent;
