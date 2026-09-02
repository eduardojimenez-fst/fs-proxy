using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Policies.UnassignPolicyFromTag;

public static class UnassignPolicyFromTagEndpoint
{
    internal static RouteHandlerBuilder MapUnassignPolicyFromTagEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/tags/{tagId:guid}/policy", async (Guid tagId, IMediator mediator, CancellationToken ct) =>
            { await mediator.Send(new UnassignPolicyFromTagCommand(tagId), ct); return Results.NoContent(); })
            .WithName("UnassignPolicyFromTag").WithSummary("Unassign the policy profile from a tag")
            .RequirePermission(ProxiesPermissions.Policies.Update);
}
