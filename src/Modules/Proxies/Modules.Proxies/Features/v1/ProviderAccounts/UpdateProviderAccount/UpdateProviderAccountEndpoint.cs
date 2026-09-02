using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.UpdateProviderAccount;

public static class UpdateProviderAccountEndpoint
{
    internal static RouteHandlerBuilder MapUpdateProviderAccountEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPut("/provider-accounts/{id:guid}",
                async (Guid id, UpdateProviderAccountBody body, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new UpdateProviderAccountCommand(id, body.Name, body.PlaintextCredentials, body.IsEnabled), ct);
                    return Results.NoContent();
                })
            .WithName("UpdateProviderAccount")
            .WithSummary("Update a proxy provider account")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.Update);
    }

    internal sealed record UpdateProviderAccountBody(string Name, string? PlaintextCredentials, bool IsEnabled);
}
