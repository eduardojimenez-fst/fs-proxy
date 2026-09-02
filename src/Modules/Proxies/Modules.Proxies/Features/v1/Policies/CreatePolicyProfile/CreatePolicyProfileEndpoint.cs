using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Policies.CreatePolicyProfile;

public static class CreatePolicyProfileEndpoint
{
    internal static RouteHandlerBuilder MapCreatePolicyProfileEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/policies", async (CreatePolicyProfileCommand command, IMediator mediator, CancellationToken ct) => Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreatePolicyProfile").WithSummary("Create a policy profile")
            .RequirePermission(ProxiesPermissions.Policies.Create);
}
