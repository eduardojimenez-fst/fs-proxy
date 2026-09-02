using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.UpdateManualProxy;

public static class UpdateManualProxyEndpoint
{
    internal static RouteHandlerBuilder MapUpdateManualProxyEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPut("/manual-proxies/{id:guid}",
                async (Guid id, UpdateManualProxyBody body, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new UpdateManualProxyCommand(id, body.Host, body.Port, body.Protocol, body.Username, body.PlaintextPassword, body.TagNames), ct);
                    return Results.NoContent();
                })
            .WithName("UpdateManualProxy")
            .WithSummary("Update a manually-hosted proxy")
            .RequirePermission(ProxiesPermissions.ManualProxies.Update);
    }

    internal sealed record UpdateManualProxyBody(
        string Host, int Port, FSH.Modules.Proxies.Contracts.ProxyProtocol Protocol,
        string? Username, string? PlaintextPassword, IReadOnlyList<string> TagNames);
}
