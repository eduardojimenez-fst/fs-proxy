using FSH.Modules.Proxies.Contracts;

namespace FSH.Modules.Proxies.Jobs;

/// <summary>
/// Builds the <see cref="System.Net.WebProxy"/> URI used to route a health-check probe through a
/// proxy. Every <see cref="ProxyProtocol"/> must map to its own scheme — probing a SOCKS5 proxy
/// over <c>http://</c> fails even when the proxy is perfectly healthy, which would let the policy
/// engine auto-disable a working proxy. <c>SocketsHttpHandler</c> understands <c>socks5://</c>
/// natively on modern .NET.
/// </summary>
public static class ProxyProbeUriBuilder
{
    public static Uri Build(ProxyProtocol protocol, string host, int port) =>
        new($"{SchemeFor(protocol)}://{host}:{port}");

    public static string SchemeFor(ProxyProtocol protocol) => protocol switch
    {
        ProxyProtocol.Http => "http",
        ProxyProtocol.Https => "https",
        ProxyProtocol.Socks5 => "socks5",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unsupported proxy protocol.")
    };
}
