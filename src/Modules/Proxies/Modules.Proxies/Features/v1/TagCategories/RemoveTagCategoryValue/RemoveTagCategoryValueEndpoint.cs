using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.RemoveTagCategoryValue;

public static class RemoveTagCategoryValueEndpoint
{
    internal static RouteHandlerBuilder MapRemoveTagCategoryValueEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/tag-categories/{id:guid}/values/{value}", async (Guid id, string value, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new RemoveTagCategoryValueCommand(id, value), ct);
                return Results.NoContent();
            })
            .WithName("RemoveTagCategoryValue").WithSummary("Remove a value from a tag category")
            .RequirePermission(ProxiesPermissions.Tags.Update);
}
