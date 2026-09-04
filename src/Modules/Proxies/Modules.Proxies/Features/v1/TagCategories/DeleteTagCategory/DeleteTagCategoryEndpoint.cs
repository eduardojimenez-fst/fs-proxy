using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.DeleteTagCategory;

public static class DeleteTagCategoryEndpoint
{
    internal static RouteHandlerBuilder MapDeleteTagCategoryEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/tag-categories/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) => { await mediator.Send(new DeleteTagCategoryCommand(id), ct); return Results.NoContent(); })
            .WithName("DeleteTagCategory").WithSummary("Delete a tag category")
            .RequirePermission(ProxiesPermissions.Tags.Delete);
}
