using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.CreateHealthCheckTarget;

public static class CreateHealthCheckTargetEndpoint
{
    internal static RouteHandlerBuilder MapCreateHealthCheckTargetEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/health-check-targets", async (CreateHealthCheckTargetCommand command, IMediator mediator, CancellationToken ct) => Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateHealthCheckTarget").WithSummary("Create a health check target")
            .RequirePermission(ProxiesPermissions.HealthCheckTargets.Create);
}
