namespace FSH.Modules.Proxies.Services;

public interface IPolicyEvaluationService
{
    Task EvaluateAsync(Guid proxyId, CancellationToken cancellationToken);
}
