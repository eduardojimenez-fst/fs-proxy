using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Policies.AssignPolicyToTag;

public static class AssignPolicyToTagEndpoint
{
    internal static RouteHandlerBuilder MapAssignPolicyToTagEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/tags/{tagId:guid}/policy/{policyProfileId:guid}", async (Guid tagId, Guid policyProfileId, IMediator mediator, CancellationToken ct) =>
            { await mediator.Send(new AssignPolicyToTagCommand(tagId, policyProfileId), ct); return Results.NoContent(); })
            .WithName("AssignPolicyToTag").WithSummary("Assign a policy profile to a tag")
            .RequirePermission(ProxiesPermissions.Policies.Update);
}
