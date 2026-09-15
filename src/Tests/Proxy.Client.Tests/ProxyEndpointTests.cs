using FSH.Proxy.Client;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests;

public sealed class ProxyEndpointTests
{
    private static ProxyEndpoint Sample(string? user = "u", string? password = "s3cret") =>
        new(Guid.NewGuid(), "203.0.113.10", 8080, user, password);

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
