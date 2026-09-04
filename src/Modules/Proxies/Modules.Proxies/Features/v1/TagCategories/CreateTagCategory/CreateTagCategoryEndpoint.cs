using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.CreateTagCategory;

public static class CreateTagCategoryEndpoint
{
    internal static RouteHandlerBuilder MapCreateTagCategoryEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/tag-categories", async (CreateTagCategoryCommand command, IMediator mediator, CancellationToken ct) => Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateTagCategory").WithSummary("Create a tag category")
            .RequirePermission(ProxiesPermissions.Tags.Create);
}
