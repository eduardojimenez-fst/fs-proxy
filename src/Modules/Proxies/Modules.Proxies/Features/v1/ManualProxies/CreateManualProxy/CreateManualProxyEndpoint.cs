using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;

public static class CreateManualProxyEndpoint
{
    internal static RouteHandlerBuilder MapCreateManualProxyEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/manual-proxies",
                async (CreateManualProxyCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateManualProxy")
            .WithSummary("Create a manually-hosted proxy")
            .RequirePermission(ProxiesPermissions.ManualProxies.Create);
    }
}
