using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.AssignProxyTag;

public static class AssignProxyTagEndpoint
{
    internal static RouteHandlerBuilder MapAssignProxyTagEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/tags/assign", (AssignProxyTagCommand command, IMediator mediator, CancellationToken ct) => mediator.Send(command, ct))
            .WithName("AssignProxyTag").WithSummary("Assign a tag to one or more proxies")
            .RequirePermission(ProxiesPermissions.Tags.Update);
}
