using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.ListApiClients;

public static class ListApiClientsEndpoint
{
    internal static RouteHandlerBuilder MapListApiClientsEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api-clients", (IMediator mediator, CancellationToken ct) => mediator.Send(new ListApiClientsQuery(), ct))
            .WithName("ListApiClients").WithSummary("List API clients (keys never included)")
            .RequirePermission(ProxiesPermissions.ApiClients.View);
}
