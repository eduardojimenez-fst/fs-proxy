using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountFromFile;

public static class SyncProviderAccountFromFileEndpoint
{
    internal static RouteHandlerBuilder MapSyncProviderAccountFromFileEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/provider-accounts/{id:guid}/sync-from-file",
                async (Guid id, [FromForm] IFormFile file, [FromForm] string? defaultUsername,
                    [FromForm] string? defaultPassword, [FromForm] string? defaultGeolocation,
                    [FromForm] string? defaultProxyKind, IMediator mediator, CancellationToken ct) =>
                {
                    ProxyKind? kind = null;
                    if (!string.IsNullOrWhiteSpace(defaultProxyKind))
                    {
                        if (!Enum.TryParse(defaultProxyKind, ignoreCase: true, out ProxyKind parsedKind))
                        {
                            return Results.BadRequest(new
                            {
                                title = "Invalid defaultProxyKind",
                                detail = $"\"{defaultProxyKind}\" is not a recognized proxy kind (DataCenter, Residential, Mobile, Dedicated).",
                            });
                        }
                        kind = parsedKind;
                    }

                    using var reader = new StreamReader(file.OpenReadStream());
                    var content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                    var result = await mediator.Send(
                        new SyncProviderAccountFromFileCommand(id, content, defaultUsername, defaultPassword, defaultGeolocation, kind),
                        ct).ConfigureAwait(false);
                    return Results.Ok(result);
                })
            .DisableAntiforgery()
            .WithName("SyncProviderAccountFromFile")
            .WithSummary("Sync a provider account's proxies from an uploaded canonical-format CSV file")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.Update);
}
