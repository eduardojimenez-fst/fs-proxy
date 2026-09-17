using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Policies.GetProxyPolicyResolution;

public static class GetProxyPolicyResolutionEndpoint
{
    internal static RouteHandlerBuilder MapGetProxyPolicyResolutionEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/{id:guid}/policy-resolution",
                (Guid id, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new GetProxyPolicyResolutionQuery(id), ct))
            .WithName("GetProxyPolicyResolution")
            .WithSummary("Explain which policy profile and health-check targets apply to a proxy, and how close it is to tripping")
            .RequirePermission(ProxiesPermissions.Policies.View);
    }
}
