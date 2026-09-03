using Microsoft.AspNetCore.Authorization;

namespace FSH.Modules.Proxies.Authentication;

/// <summary>
/// Marker requirement for the <c>ProxiesConsumerAccess</c> policy. Satisfied by
/// <see cref="ProxiesConsumerAuthorizationHandler"/>: either the caller authenticated with an
/// admin-issued API key, or the caller holds
/// <c>ProxiesPermissions.Consumers.Request</c> on their JWT identity.
/// </summary>
public sealed class ProxiesConsumerRequirement : IAuthorizationRequirement;
