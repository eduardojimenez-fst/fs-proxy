using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.DeleteApiClient;

public static class DeleteApiClientEndpoint
{
    internal static RouteHandlerBuilder MapDeleteApiClientEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/api-clients/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) => { await mediator.Send(new DeleteApiClientCommand(id), ct); return Results.NoContent(); })
            .WithName("DeleteApiClient").WithSummary("Revoke an API key")
            .RequirePermission(ProxiesPermissions.ApiClients.Delete);
}
