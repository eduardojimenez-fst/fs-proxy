namespace FSH.Modules.Proxies.Services;

public interface IHealthCheckTargetResolver
{
    Task<IReadOnlyList<ResolvedHealthCheckTarget>> ResolveTargetsAsync(Guid proxyId, CancellationToken cancellationToken);
}
