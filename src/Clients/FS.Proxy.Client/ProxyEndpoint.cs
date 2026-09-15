using System;
using System.Globalization;
using System.Net;

namespace FSH.Proxy.Client;

/// <summary>
/// One usable proxy. The only type a legacy <c>WebRequest</c>-based scraper needs to touch.
/// </summary>
public sealed class ProxyEndpoint
{
    public ProxyEndpoint(Guid id, string host, int port, ProxyProtocol protocol, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));

        // ToWebProxy() only attaches credentials when Username is non-empty (see its own remarks) —
        // a null/empty username with a non-null password would silently produce an ANONYMOUS proxy.
        // Auth then fails against a proxy that actually requires it, and the SDK reports Failure
        // against a proxy whose only real problem is this endpoint's own invalid construction.
        // Reachable via manual proxy creation on the server, so this is rejected here rather than
        // left to surface as a confusing runtime auth failure two layers away.
        if (string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            throw new ArgumentException(
                "A password was supplied with no username. An anonymous proxy (no username) must " +
                "not also carry a password — ToWebProxy() would silently drop it, producing an " +
                "unauthenticated request against a proxy that expects credentials.",
                nameof(password));
        }

        Id = id;
        Host = host.Trim();
        Port = port;
        Protocol = protocol;
        Username = username;
        Password = password;
    }

    /// <summary>The service's identifier for this proxy. Required to report an outcome against it.</summary>
    public Guid Id { get; }

    public string Host { get; }
    public int Port { get; }

    /// <summary>
    /// The scheme this proxy speaks. Required, not defaulted: a caller-supplied default here would
    /// let a future call site silently mislabel a SOCKS5 or HTTPS proxy as plain HTTP — exactly the
    /// class of silent-corruption bug this SDK exists to prevent elsewhere.
    /// </summary>
    public ProxyProtocol Protocol { get; }

    public string? Username { get; }
    public string? Password { get; }

    /// <summary>
    /// Builds an <see cref="IWebProxy"/> for <c>HttpClientHandler.Proxy</c> or <c>WebRequest.Proxy</c>.
    /// The returned proxy's <see cref="WebProxy.Address"/> carries the scheme <see cref="Protocol"/>
    /// calls for (<c>http</c>, <c>https</c>, or <c>socks5</c>) — never a bare <c>http://</c> address
    /// regardless of the actual protocol.
    /// </summary>
    /// <remarks>
    /// On net6.0+, <c>SocketsHttpHandler</c> — the default handler behind <c>HttpClient</c> —
    /// understands a <c>socks5://</c> proxy address and will actually tunnel through it. On
    /// netstandard2.0 targets (.NET Framework via <c>HttpWebRequest</c> / <c>WebRequest.Proxy</c>),
    /// there is no SOCKS support in that stack at all, full stop. The scheme is still carried
    /// correctly on the returned <see cref="WebProxy"/> there too — a caller handing it to its own
    /// SOCKS-capable client still gets the right address — but assigning it straight to
    /// <c>WebRequest.Proxy</c> on netstandard2.0 will not tunnel through a
    /// <see cref="ProxyProtocol.Socks5"/> proxy; that stack has nothing that would make it work.
    /// </remarks>
    public IWebProxy ToWebProxy()
    {
        var proxy = new WebProxy(BuildProxyUri());
        if (!string.IsNullOrEmpty(Username))
        {
            proxy.Credentials = ToCredential();
        }
        return proxy;
    }

    private Uri BuildProxyUri()
    {
        string scheme = Protocol switch
        {
            ProxyProtocol.Https => Uri.UriSchemeHttps,
            ProxyProtocol.Socks5 => "socks5",
            _ => Uri.UriSchemeHttp,
        };
        return new Uri(string.Format(CultureInfo.InvariantCulture, "{0}://{1}:{2}", scheme, Host, Port));
    }

    public NetworkCredential ToCredential() => new(Username, Password);

    /// <summary>
    /// Deliberately masks the password. <c>/request</c> returns credentials in the clear, and the
    /// most likely way they reach a log file is someone interpolating an endpoint into a message.
    /// </summary>
    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0}:{1} ({2})", Host, Port,
            string.IsNullOrEmpty(Username) ? "anonymous" : Username + ":***");
}
