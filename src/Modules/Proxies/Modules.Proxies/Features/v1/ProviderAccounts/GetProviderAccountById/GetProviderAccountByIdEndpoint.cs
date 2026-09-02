using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.GetProviderAccountById;

public static class GetProviderAccountByIdEndpoint
{
    internal static RouteHandlerBuilder MapGetProviderAccountByIdEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/provider-accounts/{id:guid}",
                (Guid id, IMediator mediator, CancellationToken ct) => mediator.Send(new GetProviderAccountByIdQuery(id), ct))
            .WithName("GetProviderAccountById")
            .WithSummary("Get a proxy provider account by id")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.View);
    }
}
