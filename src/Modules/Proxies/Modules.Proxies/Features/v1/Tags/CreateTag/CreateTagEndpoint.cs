using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Tags;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Tags.CreateTag;

public static class CreateTagEndpoint
{
    internal static RouteHandlerBuilder MapCreateTagEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/tags",
                async (CreateTagCommand command, IMediator mediator, CancellationToken ct) => Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateTag")
            .WithSummary("Create a proxy tag")
            .RequirePermission(ProxiesPermissions.Tags.Create);
    }
}
