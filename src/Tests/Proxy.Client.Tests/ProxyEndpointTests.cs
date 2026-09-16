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

    // Promoted ledger minor: ToWebProxy() only attaches credentials when Username is non-empty (see
    // its own remarks), so a null-username/non-null-password endpoint would silently become an
    // ANONYMOUS proxy — auth then fails, and the SDK reports Failure against a proxy whose only real
    // problem is this endpoint's own invalid construction. Rejected at the source instead.
    [Fact]
    public void Constructor_Should_Reject_A_Password_With_No_Username()
    {
        Should.Throw<ArgumentException>(() => Sample(user: null, password: "s3cret"));
    }

    // Companion: an empty (not merely null) username with a password must be rejected the same way —
    // ToWebProxy()'s own check is IsNullOrEmpty, not merely "is null".
    [Fact]
    public void Constructor_Should_Reject_A_Password_With_An_Empty_Username()
    {
        Should.Throw<ArgumentException>(() => Sample(user: string.Empty, password: "s3cret"));
    }

    // Companion: no username and no password is a legitimate, intentionally-anonymous proxy — must
    // NOT be rejected.
    [Fact]
    public void Constructor_Should_Accept_No_Username_And_No_Password()
    {
        Should.NotThrow(() => Sample(user: null, password: null));
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
