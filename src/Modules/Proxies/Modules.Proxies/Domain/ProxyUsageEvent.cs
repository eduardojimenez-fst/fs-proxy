using FSH.Framework.Core.Domain;

namespace FSH.Modules.Proxies.Domain;

public enum UsageEventSource { SystemHealthCheck, ConsumerFeedback }
public enum UsageEventOutcome { Success, Failure, Banned, Timeout }

public sealed class ProxyUsageEvent : BaseEntity<Guid>, IGlobalEntity
{
    public Guid ProxyId { get; private set; }
    public UsageEventSource Source { get; private set; }
    public UsageEventOutcome Outcome { get; private set; }
    public Guid? HealthCheckTargetId { get; private set; }
    public Guid? ReportedByApiClientId { get; private set; }
    public string? Detail { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    private ProxyUsageEvent() { }

    public static ProxyUsageEvent Create(
        Guid proxyId, UsageEventSource source, UsageEventOutcome outcome,
        Guid? healthCheckTargetId, Guid? reportedByApiClientId, string? detail)
    {
        return new ProxyUsageEvent
        {
            Id = Guid.CreateVersion7(),
            ProxyId = proxyId,
            Source = source,
            Outcome = outcome,
            HealthCheckTargetId = healthCheckTargetId,
            ReportedByApiClientId = reportedByApiClientId,
            Detail = detail,
            OccurredAtUtc = DateTime.UtcNow
        };
    }
}
