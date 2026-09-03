using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Providers;

public sealed class ManualAdapterTests
{
    private readonly ManualAdapter _sut = new();

    [Fact]
    public void ProviderType_Should_BeManual() => _sut.ProviderType.ShouldBe(ProxyProviderType.Manual);

    [Fact]
    public void SupportsSync_And_SupportsRenew_Should_BeFalse()
    {
        _sut.SupportsSync.ShouldBeFalse();
        _sut.SupportsRenew.ShouldBeFalse();
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ReturnEmptySuccess()
    {
        var account = ProviderAccount.Create("manual", ProxyProviderType.Manual, "n/a");

        var result = await _sut.SyncProxiesAsync(account, "n/a", CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.ShouldBeEmpty();
    }

    [Fact]
    public async Task RenewProxyAsync_Should_ReturnUnsuccessful()
    {
        var account = ProviderAccount.Create("manual", ProxyProviderType.Manual, "n/a");
        var proxy = Proxy.Create(account.Id, "1.2.3.4", 8080, ProxyProtocol.Http, null, null, null);

        var result = await _sut.RenewProxyAsync(account, "n/a", proxy, CancellationToken.None);

        result.Success.ShouldBeFalse();
    }
}
