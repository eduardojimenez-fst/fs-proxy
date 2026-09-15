using System.Net;
using FSH.Proxy.Client;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests;

public sealed class ProxyEndpointTests
{
    private static ProxyEndpoint Sample(string? user = "u", string? password = "s3cret", ProxyProtocol protocol = ProxyProtocol.Http) =>
        new(Guid.NewGuid(), "203.0.113.10", 8080, protocol, user, password);

    [Fact]
    public void ToWebProxy_Should_Carry_Address_And_Credentials()
    {
        var endpoint = Sample();

        var webProxy = endpoint.ToWebProxy();

        webProxy.ShouldNotBeNull();
        var credential = webProxy.Credentials.ShouldBeOfType<System.Net.NetworkCredential>();
        credential.UserName.ShouldBe("u");
        credential.Password.ShouldBe("s3cret");
    }

    [Fact]
    public void ToWebProxy_Should_Omit_Credentials_When_The_Proxy_Is_Open()
    {
        var endpoint = Sample(user: null, password: null);

        endpoint.ToWebProxy().Credentials.ShouldBeNull();
    }

    [Theory]
    [InlineData(ProxyProtocol.Http, "http")]
    [InlineData(ProxyProtocol.Https, "https")]
    [InlineData(ProxyProtocol.Socks5, "socks5")]
    public void ToWebProxy_Should_Carry_The_Protocols_Scheme(ProxyProtocol protocol, string expectedScheme)
    {
        // A dropped/mislabeled protocol dials the wrong scheme with no error: every request through
        // a Https or Socks5 proxy fails, the client reports Failure, and the policy engine disables
        // a perfectly healthy proxy over a client-side bug — not a server-side signal.
        var endpoint = Sample(protocol: protocol);

        var webProxy = endpoint.ToWebProxy().ShouldBeOfType<WebProxy>();

        webProxy.Address!.Scheme.ShouldBe(expectedScheme);
    }

    [Fact]
    public void ToString_Should_Never_Reveal_The_Password()
    {
        // /request returns passwords in the clear. They live in memory and on the snapshot cache,
        // and must not reach a log through a careless interpolation of the endpoint.
        var endpoint = Sample();

        var text = endpoint.ToString();

        text.ShouldNotContain("s3cret");
        text.ShouldContain("203.0.113.10");
    }
}
