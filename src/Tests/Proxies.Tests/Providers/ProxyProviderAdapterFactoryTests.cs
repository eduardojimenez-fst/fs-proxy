using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Providers;

public sealed class ProxyProviderAdapterFactoryTests
{
    [Fact]
    public void GetAdapter_Should_ReturnMatchingAdapter()
    {
        var manualAdapter = Substitute.For<IProxyProviderAdapter>();
        manualAdapter.ProviderType.Returns(ProxyProviderType.Manual);
        var webShareAdapter = Substitute.For<IProxyProviderAdapter>();
        webShareAdapter.ProviderType.Returns(ProxyProviderType.WebShare);
        var sut = new ProxyProviderAdapterFactory([manualAdapter, webShareAdapter]);

        var result = sut.GetAdapter(ProxyProviderType.WebShare);

        result.ShouldBeSameAs(webShareAdapter);
    }

    [Fact]
    public void GetAdapter_Should_Throw_When_NoAdapterRegistered()
    {
        var sut = new ProxyProviderAdapterFactory([]);

        Should.Throw<FSH.Framework.Core.Exceptions.NotFoundException>(() => sut.GetAdapter(ProxyProviderType.Oxylabs));
    }
}
