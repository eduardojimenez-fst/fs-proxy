using System.Net;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Proxies.Jobs;

public sealed class ProxyActiveHealthCheckJob(
    ProxiesDbContext dbContext, IHealthCheckTargetResolver targetResolver, IProxyPasswordResolver passwordResolver,
    IPolicyEvaluationService policyEvaluationService, ILogger<ProxyActiveHealthCheckJob> logger)
{
    [AutomaticRetry(Attempts = 0)] // a single proxy's connectivity failure IS the signal being measured — never retry the batch
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var activeProxyIds = await dbContext.Proxies
            .Where(p => p.Status == ProxyStatus.Active)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var proxyId in activeProxyIds)
        {
            try
            {
                await CheckOneProxyAsync(proxyId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Active health check failed unexpectedly for proxy {ProxyId}.", proxyId);
            }
        }
    }

    private async Task CheckOneProxyAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var proxy = await dbContext.Proxies.FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken).ConfigureAwait(false);
        if (proxy is null) return;

        var targets = await targetResolver.ResolveTargetsAsync(proxyId, cancellationToken).ConfigureAwait(false);
        var password = passwordResolver.Decrypt(proxy);

        foreach (var target in targets)
        {
            var (outcome, detail) = await ProbeAsync(proxy, password, target, cancellationToken).ConfigureAwait(false);

            dbContext.ProxyUsageEvents.Add(ProxyUsageEvent.Create(
                proxyId, UsageEventSource.SystemHealthCheck, outcome, target.TargetId, reportedByApiClientId: null, detail));
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await policyEvaluationService.EvaluateAsync(proxyId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(UsageEventOutcome Outcome, string? Detail)> ProbeAsync(
        Proxy proxy, string? password, ResolvedHealthCheckTarget target, CancellationToken cancellationToken)
    {
        var webProxy = new WebProxy($"{(proxy.Protocol == ProxyProtocol.Https ? "https" : "http")}://{proxy.Host}:{proxy.Port}");
        if (!string.IsNullOrEmpty(proxy.Username))
        {
            webProxy.Credentials = new NetworkCredential(proxy.Username, password);
        }
        using var handler = new SocketsHttpHandler { Proxy = webProxy, UseProxy = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(target.TimeoutMs) };

        try
        {
            using var response = await client.GetAsync(new Uri(target.TestUrl), cancellationToken).ConfigureAwait(false);
            string body = target.ExpectedBodyKeyword is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var outcome = ProxyHealthCheckOutcomeClassifier.Classify(false, response.StatusCode, body, target.ExpectedStatusCode, target.ExpectedBodyKeyword);
            return (outcome, outcome == UsageEventOutcome.Success ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (TaskCanceledException)
        {
            return (UsageEventOutcome.Timeout, "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return (UsageEventOutcome.Failure, ex.Message);
        }
    }
}
