using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Jobs;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Jobs;

public sealed class ProxyProbeUriBuilderTests
{
    [Theory]
    [InlineData(ProxyProtocol.Http, "http")]
    [InlineData(ProxyProtocol.Https, "https")]
    [InlineData(ProxyProtocol.Socks5, "socks5")]
    public void SchemeFor_Should_MapEveryProtocolToItsOwnScheme(ProxyProtocol protocol, string expected) =>
        ProxyProbeUriBuilder.SchemeFor(protocol).ShouldBe(expected);

    [Fact]
    public void Build_Should_ProduceSocks5Uri_When_ProxyIsSocks5() =>
        ProxyProbeUriBuilder.Build(ProxyProtocol.Socks5, "1.2.3.4", 1080)
            .ToString().ShouldStartWith("socks5://1.2.3.4:1080");

    [Fact]
    public void Build_Should_ProduceHttpUri_When_ProxyIsHttp() =>
        ProxyProbeUriBuilder.Build(ProxyProtocol.Http, "1.2.3.4", 8080)
            .ToString().ShouldStartWith("http://1.2.3.4:8080");

    [Fact]
    public void SchemeFor_Should_Throw_When_ProtocolIsNotKnown() =>
        Should.Throw<ArgumentOutOfRangeException>(() => ProxyProbeUriBuilder.SchemeFor((ProxyProtocol)99));
}
