using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.DisableProxies;

public static class DisableProxiesEndpoint
{
    internal static RouteHandlerBuilder MapDisableProxiesEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/disable",
                (FSH.Modules.Proxies.Features.v1.Proxies.EnableProxies.EnableProxiesEndpoint.SetProxiesStatusBody body, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new SetProxiesStatusCommand(body.ProxyIds, body.TagId, ProxyStatus.Disabled), ct))
            .WithName("DisableProxies")
            .WithSummary("Disable one or more proxies, by id list or by tag")
            .RequirePermission(ProxiesPermissions.ManualProxies.Update);
    }
}
