using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Events;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

/// <summary>
/// Covers the admin-attention outbox publish added on top of <see cref="ProviderAccountSyncService"/>
/// (Task 17): once a provider account's consecutive sync-failure count crosses
/// <c>SyncFailureNotificationThreshold</c>, a <see cref="ProviderAccountSyncFailedIntegrationEvent"/>
/// is written to the outbox so <c>Modules.Notifications</c> can react.
/// </summary>
public sealed class ProviderAccountSyncServiceNotificationTests
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
    public async Task SyncAsync_Should_PublishSyncFailedEvent_When_FailureThresholdReached()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Oxylabs", ProxyProviderType.Oxylabs, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.ProviderType.Returns(ProxyProviderType.Oxylabs);
        adapter.SupportsSync.Returns(true);
        adapter.SyncProxiesAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProviderSyncResult.Failed("401"));
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.Oxylabs).Returns(adapter);
        var outbox = Substitute.For<IOutboxWriter>();

        var sut = new ProviderAccountSyncService(db, factory, new FakeProtector(), outbox);

        // Third consecutive failure crosses the threshold (>=3).
        await sut.SyncAsync(account.Id, CancellationToken.None);
        await sut.SyncAsync(account.Id, CancellationToken.None);
        await sut.SyncAsync(account.Id, CancellationToken.None);

        await outbox.Received(1).AddAsync(Arg.Any<ProviderAccountSyncFailedIntegrationEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_Should_NotPublish_When_FailureCountBelowThreshold()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Oxylabs", ProxyProviderType.Oxylabs, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.ProviderType.Returns(ProxyProviderType.Oxylabs);
        adapter.SupportsSync.Returns(true);
        adapter.SyncProxiesAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProviderSyncResult.Failed("401"));
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.Oxylabs).Returns(adapter);
        var outbox = Substitute.For<IOutboxWriter>();

        var sut = new ProviderAccountSyncService(db, factory, new FakeProtector(), outbox);

        // Two failures — below the >=3 threshold.
        await sut.SyncAsync(account.Id, CancellationToken.None);
        await sut.SyncAsync(account.Id, CancellationToken.None);

        await outbox.DidNotReceiveWithAnyArgs().AddAsync(Arg.Any<ProviderAccountSyncFailedIntegrationEvent>(), default);
    }

    [Fact]
    public async Task SyncAsync_Should_NotPublish_When_SyncSucceeds()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.ProviderType.Returns(ProxyProviderType.WebShare);
        adapter.SupportsSync.Returns(true);
        adapter.SyncProxiesAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProviderSyncResult.Ok([]));
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.WebShare).Returns(adapter);
        var outbox = Substitute.For<IOutboxWriter>();

        var sut = new ProviderAccountSyncService(db, factory, new FakeProtector(), outbox);

        var touched = await sut.SyncAsync(account.Id, CancellationToken.None);

        touched.ShouldBe(0);
        await outbox.DidNotReceiveWithAnyArgs().AddAsync(Arg.Any<ProviderAccountSyncFailedIntegrationEvent>(), default);
    }
}
