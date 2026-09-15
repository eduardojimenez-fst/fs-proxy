namespace FSH.Proxy.Client;

/// <summary>
/// The result of one attempt made through a proxy, as the service's policy engine understands it.
/// </summary>
/// <remarks>
/// Mirrors <c>FSH.Modules.Proxies.Contracts.UsageEventOutcome</c> member-for-member and in the same
/// order, so the wire mapping is the member name itself. The service serializes this enum as a
/// string, so renaming a member here is a breaking protocol change, not a refactor.
/// </remarks>
public enum ProxyOutcome
{
    /// <summary>The proxy did its job. The destination's own errors (4xx, 5xx) count as success.</summary>
    Success,

    /// <summary>The proxy is broken or misauthenticated — refused the connection, failed to resolve, or rejected the credentials.</summary>
    Failure,

    /// <summary>The destination recognized and rejected the proxy's IP.</summary>
    Banned,

    /// <summary>The proxy accepted the connection and never answered.</summary>
    Timeout,
}
