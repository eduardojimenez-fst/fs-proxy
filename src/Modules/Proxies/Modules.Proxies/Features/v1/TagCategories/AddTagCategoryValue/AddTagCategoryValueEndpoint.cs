using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.AddTagCategoryValue;

public static class AddTagCategoryValueEndpoint
{
    internal static RouteHandlerBuilder MapAddTagCategoryValueEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/tag-categories/{id:guid}/values", async (Guid id, AddTagCategoryValueBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new AddTagCategoryValueCommand(id, body.Value), ct);
                return Results.NoContent();
            })
            .WithName("AddTagCategoryValue").WithSummary("Add a value to a tag category")
            .RequirePermission(ProxiesPermissions.Tags.Update);

    internal sealed record AddTagCategoryValueBody(string Value);
}
