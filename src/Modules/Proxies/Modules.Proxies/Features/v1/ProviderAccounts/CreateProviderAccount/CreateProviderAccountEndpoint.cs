using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.CreateProviderAccount;

public static class CreateProviderAccountEndpoint
{
    internal static RouteHandlerBuilder MapCreateProviderAccountEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/provider-accounts",
                async (CreateProviderAccountCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateProviderAccount")
            .WithSummary("Create a proxy provider account")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.Create);
    }
}
