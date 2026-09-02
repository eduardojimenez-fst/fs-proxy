using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Tags;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Tags.ListTags;

public static class ListTagsEndpoint
{
    internal static RouteHandlerBuilder MapListTagsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/tags", (IMediator mediator, CancellationToken ct) => mediator.Send(new ListTagsQuery(), ct))
            .WithName("ListTags")
            .WithSummary("List all proxy tags")
            .RequirePermission(ProxiesPermissions.Tags.View);
    }
}
