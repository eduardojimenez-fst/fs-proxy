namespace FSH.Modules.Proxies.Contracts.Dtos;

/// <summary>
/// A health-check target that will actually be probed for a proxy. <c>Id</c> and <c>FromTag</c>
/// are null for the configured fallback target, used when no tag of this proxy assigns one.
/// </summary>
public sealed record ResolvedHealthCheckTargetDto(
    Guid? Id, string Name, string TestUrl, int? ExpectedStatusCode, string? ExpectedBodyKeyword,
    int TimeoutMs, string? FromTag);

/// <summary>One profile competing to govern a proxy, and the tag that mapped it.</summary>
public sealed record PolicyCandidateDto(Guid TagId, string TagName, PolicyProfileDto Profile, bool IsWinner);

/// <summary>
/// Explains what the auto-disable engine would do with a proxy right now: which profile governs
/// it and why, how close it is to tripping, and where its health checks are actually pointed.
/// Built from the same resolver the engine uses, so it cannot drift from the real decision.
///
/// <c>Policy</c> is null when the proxy has no tags, or no tag maps to a profile — in which case
/// nothing is ever auto-disabled, no matter how many failures accumulate. <c>FailuresInWindow</c>
/// and <c>DistinctReportersInWindow</c> are then null too: with no profile there is no window to
/// count in.
/// </summary>
public sealed record ProxyPolicyResolutionDto(
    Guid ProxyId,
    IReadOnlyList<string> Tags,
    PolicyProfileDto? Policy,
    string? PolicyFromTag,
    IReadOnlyList<PolicyCandidateDto> Candidates,
    int? FailuresInWindow,
    int? DistinctReportersInWindow,
    DateTime? WindowStartUtc,
    IReadOnlyList<ResolvedHealthCheckTargetDto> HealthCheckTargets,
    bool UsingDefaultHealthCheckTarget);
