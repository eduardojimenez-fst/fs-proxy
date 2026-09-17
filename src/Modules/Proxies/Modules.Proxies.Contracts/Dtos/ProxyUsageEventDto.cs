namespace FSH.Modules.Proxies.Contracts.Dtos;

/// <summary>
/// One entry in a proxy's usage timeline — the audit trail behind an automatic status change.
/// <see cref="ReportedByApiClientName"/> is resolved for display; it is null for system health
/// checks and for feedback whose reporter no longer exists. <see cref="ProxyUsername"/> is carried
/// alongside host/port because several providers put the proxy's own egress IP in the auth
/// username, so operators identify a proxy by it in the fleet-wide feed.
/// </summary>
public sealed record ProxyUsageEventDto(
    Guid Id, Guid ProxyId, string ProxyHost, int ProxyPort, string? ProxyUsername,
    UsageEventSource Source, UsageEventOutcome Outcome,
    Guid? ReportedByApiClientId, string? ReportedByApiClientName,
    Guid? HealthCheckTargetId, string? Detail, DateTime OccurredAtUtc);
