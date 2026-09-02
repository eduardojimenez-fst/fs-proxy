using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.AssignHealthCheckTargetToTag;

public static class AssignHealthCheckTargetToTagEndpoint
{
    internal static RouteHandlerBuilder MapAssignHealthCheckTargetToTagEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/tags/{tagId:guid}/health-check-target/{healthCheckTargetId:guid}", async (Guid tagId, Guid healthCheckTargetId, IMediator mediator, CancellationToken ct) =>
            { await mediator.Send(new AssignHealthCheckTargetToTagCommand(tagId, healthCheckTargetId), ct); return Results.NoContent(); })
            .WithName("AssignHealthCheckTargetToTag").WithSummary("Assign a health check target to a tag")
            .RequirePermission(ProxiesPermissions.HealthCheckTargets.Update);
}
