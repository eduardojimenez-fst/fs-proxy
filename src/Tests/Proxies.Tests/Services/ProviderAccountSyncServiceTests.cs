using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

public sealed class ProviderAccountSyncServiceTests
{
    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakeProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    [Fact]
    public async Task SyncAsync_Should_CreateNewProxy_UpdateExisting_And_RetireMissing()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "{}");
        var staleProxy = Proxy.Create(account.Id, "old-host", 1111, ProxyProtocol.Http, null, null, "ext-stale");
        var updatingProxy = Proxy.Create(account.Id, "old-ip", 2222, ProxyProtocol.Http, null, null, "ext-existing");
        db.ProviderAccounts.Add(account);
        db.Proxies.AddRange(staleProxy, updatingProxy);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.ProviderType.Returns(ProxyProviderType.WebShare);
        adapter.SupportsSync.Returns(true);
        adapter.SyncProxiesAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProviderSyncResult.Ok([
                new ProviderProxyRecord("ext-existing", "new-ip", 3333, ProxyProtocol.Http, "u", "p", true),
                new ProviderProxyRecord("ext-new", "9.9.9.9", 4444, ProxyProtocol.Http, "u2", "p2", true)]));
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.WebShare).Returns(adapter);

        var sut = new ProviderAccountSyncService(db, factory, new FakeProtector());

        var touched = await sut.SyncAsync(account.Id, CancellationToken.None);

        touched.ShouldBe(3);
        (await db.Proxies.SingleAsync(p => p.ExternalId == "ext-stale")).Status.ShouldBe(ProxyStatus.Retired);
        (await db.Proxies.SingleAsync(p => p.ExternalId == "ext-existing")).Host.ShouldBe("new-ip");
        (await db.Proxies.SingleAsync(p => p.ExternalId == "ext-new")).Host.ShouldBe("9.9.9.9");
        (await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id)).LastSyncStatus.ShouldNotBeNull();
    }

    [Fact]
    public async Task SyncAsync_Should_RecordFailure_When_AdapterReportsFailure()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Oxylabs", ProxyProviderType.Oxylabs, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.ProviderType.Returns(ProxyProviderType.Oxylabs);
        adapter.SupportsSync.Returns(true);
        adapter.SyncProxiesAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProviderSyncResult.Failed("401 Unauthorized"));
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.Oxylabs).Returns(adapter);

        var sut = new ProviderAccountSyncService(db, factory, new FakeProtector());

        var touched = await sut.SyncAsync(account.Id, CancellationToken.None);

        touched.ShouldBe(0);
        var stored = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        stored.ConsecutiveSyncFailures.ShouldBe(1);
        stored.LastSyncStatus!.ShouldContain("401");
    }
}
