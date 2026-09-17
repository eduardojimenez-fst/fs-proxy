using FSH.Framework.Shared.Persistence;
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.UsageEvents;

/// <summary>
/// Reads the usage-event timeline, newest first. Every filter is optional: with no
/// <see cref="ProxyId"/> this is a fleet-wide feed, with one it is a single proxy's drill-down.
/// <see cref="FailuresOnly"/> is the "why was this disabled?" view — it selects exactly the
/// events the policy engine counts (everything except <c>Success</c>).
/// </summary>
public sealed record ListProxyUsageEventsQuery(
    Guid? ProxyId = null, UsageEventOutcome? Outcome = null, UsageEventSource? Source = null,
    bool FailuresOnly = false, DateTime? FromUtc = null, DateTime? ToUtc = null,
    int PageNumber = 1, int PageSize = 20) : IQuery<PagedResponse<ProxyUsageEventDto>>;
