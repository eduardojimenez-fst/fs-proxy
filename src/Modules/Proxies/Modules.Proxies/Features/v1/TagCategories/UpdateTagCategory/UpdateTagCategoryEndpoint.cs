using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.UpdateTagCategory;

public static class UpdateTagCategoryEndpoint
{
    internal static RouteHandlerBuilder MapUpdateTagCategoryEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/tag-categories/{id:guid}", async (Guid id, UpdateTagCategoryBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateTagCategoryCommand(id, body.Name), ct);
                return Results.NoContent();
            })
            .WithName("UpdateTagCategory").WithSummary("Rename a tag category")
            .RequirePermission(ProxiesPermissions.Tags.Update);

    internal sealed record UpdateTagCategoryBody(string Name);
}
