using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.DeleteHealthCheckTarget;

public static class DeleteHealthCheckTargetEndpoint
{
    internal static RouteHandlerBuilder MapDeleteHealthCheckTargetEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/health-check-targets/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) => { await mediator.Send(new DeleteHealthCheckTargetCommand(id), ct); return Results.NoContent(); })
            .WithName("DeleteHealthCheckTarget").WithSummary("Delete a health check target")
            .RequirePermission(ProxiesPermissions.HealthCheckTargets.Delete);
}
