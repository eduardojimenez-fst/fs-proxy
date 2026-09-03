using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountNow;

public static class SyncProviderAccountNowEndpoint
{
    internal static RouteHandlerBuilder MapSyncProviderAccountNowEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/provider-accounts/{id:guid}/sync",
                (Guid id, IMediator mediator, CancellationToken ct) => mediator.Send(new SyncProviderAccountNowCommand(id), ct))
            .WithName("SyncProviderAccountNow")
            .WithSummary("Trigger an immediate sync for a provider account")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.Update);
}
