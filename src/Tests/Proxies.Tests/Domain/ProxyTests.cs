using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Domain;

public sealed class ProxyTests
{
    [Fact]
    public void Create_Should_DefaultTo_TestingStatus()
    {
        var proxy = Proxy.Create(Guid.NewGuid(), "1.2.3.4", 8080, ProxyProtocol.Http, "user", "protected-pw", "ext-1");

        proxy.Status.ShouldBe(ProxyStatus.Testing);
        proxy.Host.ShouldBe("1.2.3.4");
        proxy.Port.ShouldBe(8080);
    }

    [Fact]
    public void SetStatus_Should_UpdateStatus()
    {
        var proxy = Proxy.Create(Guid.NewGuid(), "1.2.3.4", 8080, ProxyProtocol.Http, null, null, null);

        proxy.SetStatus(ProxyStatus.Active);

        proxy.Status.ShouldBe(ProxyStatus.Active);
    }

    [Fact]
    public void MarkRenewed_Should_SetTestingStatus_And_Timestamp()
    {
        var proxy = Proxy.Create(Guid.NewGuid(), "1.2.3.4", 8080, ProxyProtocol.Http, null, null, null);
        proxy.SetStatus(ProxyStatus.Disabled);

        proxy.MarkRenewed();

        proxy.Status.ShouldBe(ProxyStatus.Testing);
        proxy.LastRenewedAtUtc.ShouldNotBeNull();
    }
}
