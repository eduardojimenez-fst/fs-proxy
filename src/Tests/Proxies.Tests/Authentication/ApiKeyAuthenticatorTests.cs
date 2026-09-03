using FSH.Modules.Proxies.Authentication;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Authentication;

public sealed class ApiKeyAuthenticatorTests
{
    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task AuthenticateAsync_Should_ReturnClient_When_KeyIsValidAndEnabled()
    {
        await using var db = CreateDb();
        var hasher = new ApiKeyHasher();
        var (plaintextKey, hash) = hasher.GenerateKey();
        var client = ApiClient.Create("TAG", hash);
        db.ApiClients.Add(client);
        await db.SaveChangesAsync();
        var sut = new ApiKeyAuthenticator(db, hasher);

        var result = await sut.AuthenticateAsync(plaintextKey, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Id.ShouldBe(client.Id);
        (await db.ApiClients.SingleAsync(x => x.Id == client.Id)).LastUsedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_Should_ReturnNull_When_KeyIsUnknown()
    {
        await using var db = CreateDb();
        var sut = new ApiKeyAuthenticator(db, new ApiKeyHasher());

        (await sut.AuthenticateAsync("not-a-real-key", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_Should_ReturnNull_When_ClientIsDisabled()
    {
        await using var db = CreateDb();
        var hasher = new ApiKeyHasher();
        var (plaintextKey, hash) = hasher.GenerateKey();
        var client = ApiClient.Create("TAG", hash);
        client.SetEnabled(false);
        db.ApiClients.Add(client);
        await db.SaveChangesAsync();
        var sut = new ApiKeyAuthenticator(db, hasher);

        (await sut.AuthenticateAsync(plaintextKey, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_Should_ReturnNull_When_KeyIsNullOrWhitespace()
    {
        await using var db = CreateDb();
        var sut = new ApiKeyAuthenticator(db, new ApiKeyHasher());

        (await sut.AuthenticateAsync(null, CancellationToken.None)).ShouldBeNull();
        (await sut.AuthenticateAsync("  ", CancellationToken.None)).ShouldBeNull();
    }
}
