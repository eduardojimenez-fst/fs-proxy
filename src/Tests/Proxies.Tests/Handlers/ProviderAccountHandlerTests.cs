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

    private sealed class FakeSecretProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string ciphertext) => ciphertext.Replace("protected:", string.Empty, StringComparison.Ordinal);
    }
}
