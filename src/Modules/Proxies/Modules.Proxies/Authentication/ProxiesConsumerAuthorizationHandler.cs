using System.Security.Claims;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Proxies.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace FSH.Modules.Proxies.Authentication;

/// <summary>
/// Authorizes the two consumer-facing endpoints (<c>POST /proxies/request</c> and
/// <c>POST /proxies/{id}/feedback</c>), which accept either the "ApiKey" scheme or the app-wide
/// JWT scheme.
///
/// The two legs are NOT equivalent. Presenting a valid API key already proves possession of an
/// admin-issued, enabled <c>ApiClient</c> secret (see <see cref="ApiKeyAuthenticator"/>) — that
/// possession IS the authorization, so no further permission check applies. A JWT identity, by
/// contrast, can belong to any authenticated user (this app permits anonymous self-registration),
/// and <c>request</c> returns decrypted proxy passwords while <c>feedback</c> drives the
/// auto-disable policy engine — so a JWT caller must additionally hold an explicit
/// <see cref="ProxiesPermissions.Consumers.Request"/> grant.
/// </summary>
public sealed class ProxiesConsumerAuthorizationHandler(IUserPermissionService userPermissionService)
    : AuthorizationHandler<ProxiesConsumerRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ProxiesConsumerRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.Identities.Any(identity =>
                identity.IsAuthenticated
                && string.Equals(identity.AuthenticationType, ApiKeyAuthenticationDefaults.SchemeName, StringComparison.Ordinal)))
        {
            context.Succeed(requirement);
            return;
        }

        string? userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        if (await userPermissionService
                .HasPermissionAsync(userId, ProxiesPermissions.Consumers.Request)
                .ConfigureAwait(false))
        {
            context.Succeed(requirement);
        }
    }
}
