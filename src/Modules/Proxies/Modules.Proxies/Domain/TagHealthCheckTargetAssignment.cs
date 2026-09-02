using FSH.Framework.Core.Domain;

namespace FSH.Modules.Proxies.Domain;

/// <summary>At most one health-check target per tag — enforced by a single-column PK on TagId.</summary>
public sealed class TagHealthCheckTargetAssignment : IGlobalEntity
{
    public Guid TagId { get; private set; }
    public Guid HealthCheckTargetId { get; private set; }

    private TagHealthCheckTargetAssignment() { }

    public static TagHealthCheckTargetAssignment Create(Guid tagId, Guid healthCheckTargetId) =>
        new() { TagId = tagId, HealthCheckTargetId = healthCheckTargetId };
}
