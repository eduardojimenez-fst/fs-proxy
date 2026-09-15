using System;
using System.Globalization;
using System.Net;

namespace FSH.Proxy.Client;

/// <summary>
/// One usable proxy. The only type a legacy <c>WebRequest</c>-based scraper needs to touch.
/// </summary>
public sealed class ProxyEndpoint
{
    public ProxyEndpoint(Guid id, string host, int port, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));

        Id = id;
        Host = host.Trim();
        Port = port;
        Username = username;
        Password = password;
    }

    /// <summary>The service's identifier for this proxy. Required to report an outcome against it.</summary>
    public Guid Id { get; }

    public string Host { get; }
    public int Port { get; }
    public string? Username { get; }
    public string? Password { get; }

    /// <summary>Builds an <see cref="IWebProxy"/> for <c>HttpClientHandler.Proxy</c> or <c>WebRequest.Proxy</c>.</summary>
    public IWebProxy ToWebProxy()
    {
        var proxy = new WebProxy(Host, Port);
        if (!string.IsNullOrEmpty(Username))
        {
            proxy.Credentials = ToCredential();
        }
        return proxy;
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
