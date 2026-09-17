using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.UsageEvents;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.UsageEvents.ListProxyUsageEvents;

public static class ListProxyUsageEventsEndpoint
{
    internal static RouteHandlerBuilder MapListProxyUsageEventsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/usage-events",
                (Guid? proxyId, UsageEventOutcome? outcome, UsageEventSource? source, bool? failuresOnly,
                    DateTime? fromUtc, DateTime? toUtc, int pageNumber, int pageSize,
                    IMediator mediator, CancellationToken ct) =>
                    mediator.Send(
                        new ListProxyUsageEventsQuery(
                            proxyId, outcome, source, failuresOnly ?? false, fromUtc, toUtc,
                            pageNumber == 0 ? 1 : pageNumber, pageSize == 0 ? 20 : pageSize),
                        ct))
            .WithName("ListProxyUsageEvents")
            .WithSummary("List proxy usage events (health-check probes + consumer feedback), newest first")
            .RequirePermission(ProxiesPermissions.UsageEvents.View);
    }
}
