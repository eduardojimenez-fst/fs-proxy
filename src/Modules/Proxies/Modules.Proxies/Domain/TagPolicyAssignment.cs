using FSH.Framework.Core.Domain;

namespace FSH.Modules.Proxies.Domain;

/// <summary>At most one policy profile per tag — enforced by a single-column PK on TagId.</summary>
public sealed class TagPolicyAssignment : IGlobalEntity
{
    public Guid TagId { get; private set; }
    public Guid PolicyProfileId { get; private set; }

    private TagPolicyAssignment() { }

    public static TagPolicyAssignment Create(Guid tagId, Guid policyProfileId) =>
        new() { TagId = tagId, PolicyProfileId = policyProfileId };
}
