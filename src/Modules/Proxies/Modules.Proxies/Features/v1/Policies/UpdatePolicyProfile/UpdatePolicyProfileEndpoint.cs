using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Policies.UpdatePolicyProfile;

public static class UpdatePolicyProfileEndpoint
{
    internal static RouteHandlerBuilder MapUpdatePolicyProfileEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/policies/{id:guid}", async (Guid id, UpdatePolicyProfileBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdatePolicyProfileCommand(id, body.Name, body.Type, body.FailureThreshold, body.WindowMinutes, body.MinDistinctReporters), ct);
                return Results.NoContent();
            })
            .WithName("UpdatePolicyProfile").WithSummary("Update a policy profile")
            .RequirePermission(ProxiesPermissions.Policies.Update);

    internal sealed record UpdatePolicyProfileBody(string Name, PolicyProfileType Type, int FailureThreshold, int WindowMinutes, int MinDistinctReporters);
}
