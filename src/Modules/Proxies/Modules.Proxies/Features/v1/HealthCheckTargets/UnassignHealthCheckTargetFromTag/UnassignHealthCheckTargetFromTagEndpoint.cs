using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UnassignHealthCheckTargetFromTag;

public static class UnassignHealthCheckTargetFromTagEndpoint
{
    internal static RouteHandlerBuilder MapUnassignHealthCheckTargetFromTagEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/tags/{tagId:guid}/health-check-target", async (Guid tagId, IMediator mediator, CancellationToken ct) =>
            { await mediator.Send(new UnassignHealthCheckTargetFromTagCommand(tagId), ct); return Results.NoContent(); })
            .WithName("UnassignHealthCheckTargetFromTag").WithSummary("Unassign the health check target from a tag")
            .RequirePermission(ProxiesPermissions.HealthCheckTargets.Update);
}
