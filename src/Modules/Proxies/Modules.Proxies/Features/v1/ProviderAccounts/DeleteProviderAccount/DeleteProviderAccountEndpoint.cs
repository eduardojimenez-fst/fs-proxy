using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.DeleteProviderAccount;

public static class DeleteProviderAccountEndpoint
{
    internal static RouteHandlerBuilder MapDeleteProviderAccountEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapDelete("/provider-accounts/{id:guid}",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new DeleteProviderAccountCommand(id), ct);
                    return Results.NoContent();
                })
            .WithName("DeleteProviderAccount")
            .WithSummary("Delete a proxy provider account")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.Delete);
    }
}
