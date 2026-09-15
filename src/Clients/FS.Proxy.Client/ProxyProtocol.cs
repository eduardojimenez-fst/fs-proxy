namespace FSH.Proxy.Client;

/// <summary>
/// The scheme a proxy speaks.
/// </summary>
/// <remarks>
/// Mirrors <c>FSH.Modules.Proxies.Contracts.ProxyProtocol</c> member-for-member and in the same
/// order, for the same reason <see cref="ProxyOutcome"/> mirrors <c>UsageEventOutcome</c>: the
/// service serializes this enum as a string, so the member name is the wire contract — renaming a
/// member here is a breaking protocol change, not a refactor.
/// </remarks>
public enum ProxyProtocol
{
    Http,
    Https,
    Socks5,
}
