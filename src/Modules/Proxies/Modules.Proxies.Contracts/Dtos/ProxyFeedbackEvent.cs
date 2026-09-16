namespace FSH.Modules.Proxies.Contracts.Dtos;

/// <summary>One reported proxy usage outcome. The unit of a batched feedback submission.</summary>
/// <remarks>
/// Deliberately carries no timestamp. <c>ProxyUsageEvent.Create</c> stamps <c>OccurredAtUtc</c> with
/// server time and accepts no external value; with policy windows measured in minutes and clients
/// flushing every ten seconds, server time is accurate enough, and accepting a client clock would
/// invite skew and manipulation for no benefit.
/// </remarks>
public sealed record ProxyFeedbackEvent(Guid ProxyId, UsageEventOutcome Outcome, string? Detail);
