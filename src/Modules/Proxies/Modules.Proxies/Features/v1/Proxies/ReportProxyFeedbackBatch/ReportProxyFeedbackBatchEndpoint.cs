using System.Security.Claims;
using FSH.Modules.Proxies.Authentication;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedbackBatch;

public static class ReportProxyFeedbackBatchEndpoint
{
    internal static RouteHandlerBuilder MapReportProxyFeedbackBatchEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/feedback/batch",
                async (ReportProxyFeedbackBatchBody body, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                {
                    string? reporterIdentifier = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    var result = await mediator.Send(
                        new ReportProxyFeedbackBatchCommand(body.Events, reporterIdentifier), ct);
                    return Results.Ok(result);
                })
            .WithName("ReportProxyFeedbackBatch")
            .WithSummary("Report the outcome of using several proxies in a single call")
            .RequireAuthorization(ApiKeyAuthenticationDefaults.ConsumerPolicyName);
    }

    internal sealed record ReportProxyFeedbackBatchBody(IReadOnlyList<ProxyFeedbackEvent> Events);
}
