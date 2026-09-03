using System.Security.Claims;
using FSH.Modules.Proxies.Authentication;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedback;

public static class ReportProxyFeedbackEndpoint
{
    internal static RouteHandlerBuilder MapReportProxyFeedbackEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/{id:guid}/feedback",
                async (Guid id, ReportProxyFeedbackBody body, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                {
                    string? reporterIdentifier = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    await mediator.Send(new ReportProxyFeedbackCommand(id, body.Outcome, body.Detail, reporterIdentifier), ct);
                    return Results.NoContent();
                })
            .WithName("ReportProxyFeedback")
            .WithSummary("Report the outcome of using a proxy")
            .RequireAuthorization(ApiKeyAuthenticationDefaults.ConsumerPolicyName);
    }

    internal sealed record ReportProxyFeedbackBody(UsageEventOutcome Outcome, string? Detail);
}
