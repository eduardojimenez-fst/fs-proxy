using FSH.Modules.Proxies.Services;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

public sealed class ApiKeyHasherTests
{
    private readonly ApiKeyHasher _sut = new();

    [Fact]
    public void GenerateKey_Should_ProduceKeyAndMatchingHash()
    {
        var (plaintextKey, hash) = _sut.GenerateKey();

        plaintextKey.ShouldNotBeNullOrWhiteSpace();
        hash.ShouldNotBeNullOrWhiteSpace();
        _sut.Hash(plaintextKey).ShouldBe(hash);
    }

    [Fact]
    public void Hash_Should_BeDeterministic()
    {
        const string key = "test-key-value";

        _sut.Hash(key).ShouldBe(_sut.Hash(key));
    }

    [Fact]
    public void GenerateKey_Should_ProduceUniqueKeysAcrossCalls()
    {
        var (first, _) = _sut.GenerateKey();
        var (second, _) = _sut.GenerateKey();

        first.ShouldNotBe(second);
    }
}
