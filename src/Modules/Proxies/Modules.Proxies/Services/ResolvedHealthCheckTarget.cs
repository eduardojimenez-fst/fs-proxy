namespace FSH.Modules.Proxies.Services;

public sealed record ResolvedHealthCheckTarget(Guid? TargetId, string TestUrl, int? ExpectedStatusCode, string? ExpectedBodyKeyword, int TimeoutMs);
