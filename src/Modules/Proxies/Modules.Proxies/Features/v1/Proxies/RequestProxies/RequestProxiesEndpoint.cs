using FSH.Modules.Proxies.Authentication;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.RequestProxies;

public static class RequestProxiesEndpoint
{
    internal static RouteHandlerBuilder MapRequestProxiesEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/request",
                (RequestProxiesBody body, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new RequestProxiesQuery(body.Tags, body.Count <= 0 ? 1 : body.Count, body.Strategy, body.SessionId), ct))
            .WithName("RequestProxies")
            .WithSummary("Request one or more proxies matching all given tags")
            .RequireAuthorization(ApiKeyAuthenticationDefaults.ConsumerPolicyName);
    }

    internal sealed record RequestProxiesBody(IReadOnlyList<string> Tags, int Count, ProxySelectionStrategy Strategy, string? SessionId);
}
