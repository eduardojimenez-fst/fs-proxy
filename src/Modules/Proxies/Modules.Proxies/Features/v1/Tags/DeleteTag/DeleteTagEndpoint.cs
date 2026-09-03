using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Tags;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Tags.DeleteTag;

public static class DeleteTagEndpoint
{
    internal static RouteHandlerBuilder MapDeleteTagEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapDelete("/tags/{id:guid}",
                async (Guid id, IMediator mediator, CancellationToken ct) => { await mediator.Send(new DeleteTagCommand(id), ct); return Results.NoContent(); })
            .WithName("DeleteTag")
            .WithSummary("Delete a proxy tag")
            .RequirePermission(ProxiesPermissions.Tags.Delete);
    }
}
