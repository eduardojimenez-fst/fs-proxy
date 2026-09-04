using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ListProxies;

public static class ListProxiesEndpoint
{
    internal static RouteHandlerBuilder MapListProxiesEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/",
                (string[]? tags, ProxyStatus? status, Guid? providerAccountId, string? geolocation, int pageNumber, int pageSize, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new ListProxiesQuery(tags, status, providerAccountId, geolocation, pageNumber == 0 ? 1 : pageNumber, pageSize == 0 ? 20 : pageSize), ct))
            .WithName("ListProxies")
            .WithSummary("List proxies (paged, filterable by tags/status/provider account/geolocation)")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.View);
    }
}
