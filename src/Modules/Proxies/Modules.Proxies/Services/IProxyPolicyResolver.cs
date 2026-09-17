using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Services;

/// <summary>One candidate profile, and the tag that mapped the proxy to it.</summary>
public sealed record PolicyCandidate(Guid TagId, string TagName, PolicyProfile Profile);

/// <summary>
/// The profile that would act on a proxy, plus every candidate that competed for it.
/// <see cref="Winner"/> is null when the proxy has no tags, or none of its tags map to a profile.
/// </summary>
public sealed record ResolvedProxyPolicy(PolicyProfile? Winner, string? WinnerTagName, IReadOnlyList<PolicyCandidate> Candidates);

/// <summary>
/// Single source of truth for "which policy applies to this proxy". Shared by the engine that
/// acts on it (<see cref="IPolicyEvaluationService"/>) and by the read model that explains it to
/// operators — if these two ever disagreed, the UI would confidently show a policy that is not
/// the one disabling the proxy.
/// </summary>
public interface IProxyPolicyResolver
{
    Task<ResolvedProxyPolicy> ResolveAsync(Guid proxyId, CancellationToken cancellationToken);
}
