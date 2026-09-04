using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.SetProxyTags;

public static class SetProxyTagsEndpoint
{
    internal static RouteHandlerBuilder MapSetProxyTagsEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/{id:guid}/tags", async (Guid id, SetProxyTagsBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new SetProxyTagsCommand(id, body.TagNames), ct);
                return Results.NoContent();
            })
            .WithName("SetProxyTags").WithSummary("Replace a proxy's full tag set")
            .RequirePermission(ProxiesPermissions.Tags.Update);

    internal sealed record SetProxyTagsBody(IReadOnlyList<string> TagNames);
}
