using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.UnassignProxyTag;

public static class UnassignProxyTagEndpoint
{
    internal static RouteHandlerBuilder MapUnassignProxyTagEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/tags/unassign", (UnassignProxyTagCommand command, IMediator mediator, CancellationToken ct) => mediator.Send(command, ct))
            .WithName("UnassignProxyTag").WithSummary("Unassign a tag from one or more proxies")
            .RequirePermission(ProxiesPermissions.Tags.Update);
}
