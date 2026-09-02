using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.ListProviderAccounts;

public static class ListProviderAccountsEndpoint
{
    internal static RouteHandlerBuilder MapListProviderAccountsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/provider-accounts",
                (int pageNumber, int pageSize, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new ListProviderAccountsQuery(pageNumber == 0 ? 1 : pageNumber, pageSize == 0 ? 20 : pageSize), ct))
            .WithName("ListProviderAccounts")
            .WithSummary("List proxy provider accounts (paged)")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.View);
    }
}
