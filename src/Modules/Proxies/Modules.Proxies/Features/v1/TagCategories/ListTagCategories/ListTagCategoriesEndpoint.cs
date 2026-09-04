using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.ListTagCategories;

public static class ListTagCategoriesEndpoint
{
    internal static RouteHandlerBuilder MapListTagCategoriesEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/tag-categories", (IMediator mediator, CancellationToken ct) => mediator.Send(new ListTagCategoriesQuery(), ct))
            .WithName("ListTagCategories").WithSummary("List tag categories with their values")
            .RequirePermission(ProxiesPermissions.Tags.View);
}
