namespace FSH.Modules.Proxies.Services;

public interface IProxyRenewalService
{
    Task TriggerAsync(Guid proxyId, CancellationToken cancellationToken);
}
