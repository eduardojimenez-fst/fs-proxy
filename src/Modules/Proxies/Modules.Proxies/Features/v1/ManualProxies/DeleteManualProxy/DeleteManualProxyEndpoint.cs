using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.DeleteManualProxy;

public static class DeleteManualProxyEndpoint
{
    internal static RouteHandlerBuilder MapDeleteManualProxyEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapDelete("/manual-proxies/{id:guid}",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new DeleteManualProxyCommand(id), ct);
                    return Results.NoContent();
                })
            .WithName("DeleteManualProxy")
            .WithSummary("Delete a manually-hosted proxy")
            .RequirePermission(ProxiesPermissions.ManualProxies.Delete);
    }
}
