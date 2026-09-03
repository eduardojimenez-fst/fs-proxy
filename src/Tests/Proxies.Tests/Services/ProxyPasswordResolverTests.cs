using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

public sealed class ProxyPasswordResolverTests
{
    private sealed class TaggedProtector(string tag) : IProxySecretProtector
    {
        public string Protect(string plaintext) => $"{tag}:{plaintext}";
        public string Unprotect(string ciphertext) => $"decrypted-by-{tag}";
    }

    [Fact]
    public void Decrypt_Should_UseManualProtector_ForManualProxies()
    {
        var sut = new ProxyPasswordResolver(new TaggedProtector("provider"), new TaggedProtector("manual"));
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, "u", "cipher", null);

        sut.Decrypt(proxy).ShouldBe("decrypted-by-manual");
    }

    [Fact]
    public void Decrypt_Should_UseProviderProtector_ForProviderSourcedProxies()
    {
        var sut = new ProxyPasswordResolver(new TaggedProtector("provider"), new TaggedProtector("manual"));
        var proxy = Proxy.Create(Guid.NewGuid(), "1.1.1.1", 80, ProxyProtocol.Http, "u", "cipher", "ext-1");

        sut.Decrypt(proxy).ShouldBe("decrypted-by-provider");
    }

    [Fact]
    public void Decrypt_Should_ReturnNull_When_ProxyHasNoPassword()
    {
        var sut = new ProxyPasswordResolver(new TaggedProtector("provider"), new TaggedProtector("manual"));
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);

        sut.Decrypt(proxy).ShouldBeNull();
    }
}
