using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.EnableProxies;

public static class EnableProxiesEndpoint
{
    internal static RouteHandlerBuilder MapEnableProxiesEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/enable",
                (SetProxiesStatusBody body, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new SetProxiesStatusCommand(body.ProxyIds, body.TagId, ProxyStatus.Active), ct))
            .WithName("EnableProxies")
            .WithSummary("Enable one or more proxies, by id list or by tag")
            .RequirePermission(ProxiesPermissions.ManualProxies.Update);
    }

    internal sealed record SetProxiesStatusBody(IReadOnlyList<Guid>? ProxyIds, Guid? TagId);
}
