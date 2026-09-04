using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.CreateProviderAccount;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.DeleteProviderAccount;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.UpdateProviderAccount;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ProviderAccountHandlerTests
{
    private static ProxiesDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return TestProxiesDbContext.Create(options);
    }

    [Fact]
    public async Task Create_Should_PersistWithEncryptedCredentials()
    {
        await using var db = CreateDb();
        var protector = new FakeSecretProtector();
        var sut = new CreateProviderAccountCommandHandler(db, protector);
        var command = new CreateProviderAccountCommand("WebShare - main", ProxyProviderType.WebShare, "plain-secret");

        var id = await sut.Handle(command, CancellationToken.None);

        var stored = await db.ProviderAccounts.SingleAsync(x => x.Id == id);
        stored.Name.ShouldBe("WebShare - main");
        stored.ProtectedCredentials.ShouldBe("protected:plain-secret");
        stored.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Update_Should_ReplaceCredentials_When_Provided()
    {
        await using var db = CreateDb();
        var protector = new FakeSecretProtector();
        var account = ProviderAccount.Create("Oxylabs", ProxyProviderType.Oxylabs, "protected:old");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var sut = new UpdateProviderAccountCommandHandler(db, protector);

        await sut.Handle(new UpdateProviderAccountCommand(account.Id, "Oxylabs - renamed", "new-secret", false), CancellationToken.None);

        var stored = await db.ProviderAccounts.SingleAsync(x => x.Id == account.Id);
        stored.Name.ShouldBe("Oxylabs - renamed");
        stored.ProtectedCredentials.ShouldBe("protected:new-secret");
        stored.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_Should_RemoveAccount()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "protected:x");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var sut = new DeleteProviderAccountCommandHandler(db);

        await sut.Handle(new DeleteProviderAccountCommand(account.Id), CancellationToken.None);

        (await db.ProviderAccounts.AnyAsync(x => x.Id == account.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_Should_Throw_When_ProxiesExist_And_NotForced()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "protected:x");
        var proxy = Proxy.Create(account.Id, "10.0.0.1", 8080, ProxyProtocol.Http, null, null, null);
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new DeleteProviderAccountCommandHandler(db);

        var ex = await Should.ThrowAsync<CustomException>(
            () => sut.Handle(new DeleteProviderAccountCommand(account.Id), CancellationToken.None).AsTask());

        ex.Message.ShouldContain("1 synced proxy");
        (await db.ProviderAccounts.AnyAsync(x => x.Id == account.Id)).ShouldBeTrue();
        (await db.Proxies.AnyAsync(x => x.Id == proxy.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_Should_CascadeDeleteProxies_When_Forced()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "protected:x");
        var proxy1 = Proxy.Create(account.Id, "10.0.0.1", 8080, ProxyProtocol.Http, null, null, null);
        var proxy2 = Proxy.Create(account.Id, "10.0.0.2", 8080, ProxyProtocol.Http, null, null, null);
        db.ProviderAccounts.Add(account);
        db.Proxies.AddRange(proxy1, proxy2);
        await db.SaveChangesAsync();
        var sut = new DeleteProviderAccountCommandHandler(db);

        await sut.Handle(new DeleteProviderAccountCommand(account.Id, Force: true), CancellationToken.None);

        (await db.ProviderAccounts.AnyAsync(x => x.Id == account.Id)).ShouldBeFalse();
        (await db.Proxies.AnyAsync(x => x.ProviderAccountId == account.Id)).ShouldBeFalse();
    }

    private sealed class FakeSecretProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string ciphertext) => ciphertext.Replace("protected:", string.Empty, StringComparison.Ordinal);
    }
}
