using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Policies.ListPolicyProfiles;

public static class ListPolicyProfilesEndpoint
{
    internal static RouteHandlerBuilder MapListPolicyProfilesEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/policies", (IMediator mediator, CancellationToken ct) => mediator.Send(new ListPolicyProfilesQuery(), ct))
            .WithName("ListPolicyProfiles").WithSummary("List policy profiles")
            .RequirePermission(ProxiesPermissions.Policies.View);
}
