using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Policies.DeletePolicyProfile;

public static class DeletePolicyProfileEndpoint
{
    internal static RouteHandlerBuilder MapDeletePolicyProfileEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/policies/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) => { await mediator.Send(new DeletePolicyProfileCommand(id), ct); return Results.NoContent(); })
            .WithName("DeletePolicyProfile").WithSummary("Delete a policy profile")
            .RequirePermission(ProxiesPermissions.Policies.Delete);
}
