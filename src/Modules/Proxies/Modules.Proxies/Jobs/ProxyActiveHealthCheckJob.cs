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
        // Testing is probed alongside Active on purpose: Proxy.Create and Proxy.MarkRenewed both
        // land a proxy in Testing, and this job is the only thing that promotes it out (see
        // CheckOneProxyAsync). Without Testing in this predicate every freshly-synced or renewed
        // proxy would sit unprobed and unusable until an admin enabled it by hand.
        var probeProxyIds = await dbContext.Proxies
            .Where(p => p.Status == ProxyStatus.Active || p.Status == ProxyStatus.Testing)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var proxyId in probeProxyIds)
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

            if (ProxyHealthCheckOutcomeClassifier.ShouldPromoteToActive(proxy.Status, outcome))
            {
                proxy.SetStatus(ProxyStatus.Active);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Proxy {ProxyId} promoted from Testing to Active after a successful health check.", proxyId);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await policyEvaluationService.EvaluateAsync(proxyId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(UsageEventOutcome Outcome, string? Detail)> ProbeAsync(
        Proxy proxy, string? password, ResolvedHealthCheckTarget target, CancellationToken cancellationToken)
    {
        var webProxy = new WebProxy(ProxyProbeUriBuilder.Build(proxy.Protocol, proxy.Host, proxy.Port));
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
