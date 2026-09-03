using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedback;

public sealed class ReportProxyFeedbackCommandHandler(ProxiesDbContext dbContext, IPolicyEvaluationService policyEvaluationService)
    : ICommandHandler<ReportProxyFeedbackCommand>
{
    public async ValueTask<Unit> Handle(ReportProxyFeedbackCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        bool proxyExists = await dbContext.Proxies.AnyAsync(p => p.Id == command.ProxyId, cancellationToken).ConfigureAwait(false);
        if (!proxyExists)
        {
            throw new NotFoundException($"Proxy {command.ProxyId} not found.");
        }

        Guid? reporterId = null;
        if (Guid.TryParse(command.ReporterIdentifier, out var parsed) &&
            await dbContext.ApiClients.AnyAsync(c => c.Id == parsed, cancellationToken).ConfigureAwait(false))
        {
            reporterId = parsed;
        }

        var usageEvent = ProxyUsageEvent.Create(
            command.ProxyId, UsageEventSource.ConsumerFeedback, command.Outcome,
            healthCheckTargetId: null, reporterId, command.Detail);
        dbContext.ProxyUsageEvents.Add(usageEvent);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await policyEvaluationService.EvaluateAsync(command.ProxyId, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
