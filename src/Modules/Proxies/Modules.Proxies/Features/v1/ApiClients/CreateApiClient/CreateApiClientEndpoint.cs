using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.CreateApiClient;

public static class CreateApiClientEndpoint
{
    internal static RouteHandlerBuilder MapCreateApiClientEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api-clients", async (CreateApiClientCommand command, IMediator mediator, CancellationToken ct) => Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateApiClient").WithSummary("Issue a new API key for a scraper/service consumer — the key is shown only in this response")
            .RequirePermission(ProxiesPermissions.ApiClients.Create);
}
