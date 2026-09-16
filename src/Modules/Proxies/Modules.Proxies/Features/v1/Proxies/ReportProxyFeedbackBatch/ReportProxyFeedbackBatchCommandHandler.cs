using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedbackBatch;

/// <summary>
/// Batched sibling of <see cref="ReportProxyFeedback.ReportProxyFeedbackCommandHandler"/>. Two
/// deliberate differences from the single-event handler:
///
/// 1. An unknown proxy is rejected, not thrown on. A proxy can be retired between the moment a client
///    leases it and the moment its buffered event is flushed; a <c>NotFoundException</c> would fail the
///    whole batch and the client would retry a submission that can never succeed.
/// 2. The policy engine runs once per distinct proxy carrying a NEGATIVE outcome, not once per event.
///    <see cref="PolicyEvaluationService"/> counts only <c>Outcome != Success</c> events, so a proxy
///    whose batch held nothing but successes cannot change any decision — evaluating it is pure cost.
/// </summary>
public sealed class ReportProxyFeedbackBatchCommandHandler(
    ProxiesDbContext dbContext, IPolicyEvaluationService policyEvaluationService)
    : ICommandHandler<ReportProxyFeedbackBatchCommand, BatchFeedbackResult>
{
    public async ValueTask<BatchFeedbackResult> Handle(
        ReportProxyFeedbackBatchCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var requestedProxyIds = command.Events.Select(e => e.ProxyId).Distinct().ToList();

        var knownProxyIds = (await dbContext.Proxies
            .Where(p => requestedProxyIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet();

        Guid? reporterId = null;
        if (Guid.TryParse(command.ReporterIdentifier, out var parsed) &&
            await dbContext.ApiClients.AnyAsync(c => c.Id == parsed, cancellationToken).ConfigureAwait(false))
        {
            reporterId = parsed;
        }

        int accepted = 0;
        foreach (var feedback in command.Events)
        {
            if (!knownProxyIds.Contains(feedback.ProxyId))
            {
                continue;
            }

            dbContext.ProxyUsageEvents.Add(ProxyUsageEvent.Create(
                feedback.ProxyId, UsageEventSource.ConsumerFeedback, feedback.Outcome,
                healthCheckTargetId: null, reporterId, feedback.Detail));
            accepted++;
        }

        if (accepted > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var proxiesToEvaluate = command.Events
            .Where(e => e.Outcome != UsageEventOutcome.Success && knownProxyIds.Contains(e.ProxyId))
            .Select(e => e.ProxyId)
            .Distinct();

        foreach (var proxyId in proxiesToEvaluate)
        {
            await policyEvaluationService.EvaluateAsync(proxyId, cancellationToken).ConfigureAwait(false);
        }

        var rejected = requestedProxyIds.Where(id => !knownProxyIds.Contains(id)).ToList();
        return new BatchFeedbackResult(accepted, rejected);
    }
}
