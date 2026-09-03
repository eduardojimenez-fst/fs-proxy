using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.ListHealthCheckTargets;

public static class ListHealthCheckTargetsEndpoint
{
    internal static RouteHandlerBuilder MapListHealthCheckTargetsEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/health-check-targets", (IMediator mediator, CancellationToken ct) => mediator.Send(new ListHealthCheckTargetsQuery(), ct))
            .WithName("ListHealthCheckTargets").WithSummary("List health check targets")
            .RequirePermission(ProxiesPermissions.HealthCheckTargets.View);
}
