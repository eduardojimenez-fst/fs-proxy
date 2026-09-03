using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UpdateHealthCheckTarget;

public static class UpdateHealthCheckTargetEndpoint
{
    internal static RouteHandlerBuilder MapUpdateHealthCheckTargetEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/health-check-targets/{id:guid}", async (Guid id, UpdateHealthCheckTargetBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateHealthCheckTargetCommand(id, body.Name, body.TestUrl, body.ExpectedStatusCode, body.ExpectedBodyKeyword, body.TimeoutMs), ct);
                return Results.NoContent();
            })
            .WithName("UpdateHealthCheckTarget").WithSummary("Update a health check target")
            .RequirePermission(ProxiesPermissions.HealthCheckTargets.Update);

    internal sealed record UpdateHealthCheckTargetBody(string Name, string TestUrl, int? ExpectedStatusCode, string? ExpectedBodyKeyword, int TimeoutMs);
}
