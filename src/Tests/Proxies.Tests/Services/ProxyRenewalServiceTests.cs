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

public sealed class ProxyRenewalServiceTests
{
    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakeProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    [Fact]
    public async Task TriggerAsync_Should_UpdateProxyAndMarkRenewed_When_AdapterSupportsRenewAndSucceeds()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "{}");
        var proxy = Proxy.Create(account.Id, "old-ip", 1111, ProxyProtocol.Http, "u", "p", "ext-1");
        proxy.SetStatus(ProxyStatus.Disabled);
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.SupportsRenew.Returns(true);
        adapter.RenewProxyAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<Proxy>(), Arg.Any<CancellationToken>())
            .Returns(ProviderRenewResult.Ok(new ProviderProxyRecord("ext-1", "new-ip", 2222, ProxyProtocol.Http, "u2", "p2", true)));
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.WebShare).Returns(adapter);
        var outbox = Substitute.For<IOutboxWriter>();

        var sut = new ProxyRenewalService(db, factory, new FakeProtector(), outbox);

        await sut.TriggerAsync(proxy.Id, CancellationToken.None);

        var stored = await db.Proxies.SingleAsync(p => p.Id == proxy.Id);
        stored.Host.ShouldBe("new-ip");
        stored.Status.ShouldBe(ProxyStatus.Testing);
        stored.LastRenewedAtUtc.ShouldNotBeNull();
        await outbox.DidNotReceive().AddAsync(Arg.Any<ManualProxyNeedsAttentionIntegrationEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerAsync_Should_PublishNeedsAttentionEvent_When_AdapterDoesNotSupportRenew()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "{}");
        var proxy = Proxy.Create(account.Id, "1.1.1.1", 1111, ProxyProtocol.Http, null, null, null);
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.SupportsRenew.Returns(false);
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.Manual).Returns(adapter);
        var outbox = Substitute.For<IOutboxWriter>();

        var sut = new ProxyRenewalService(db, factory, new FakeProtector(), outbox);

        await sut.TriggerAsync(proxy.Id, CancellationToken.None);

        await outbox.Received(1).AddAsync(
            Arg.Is<ManualProxyNeedsAttentionIntegrationEvent>(e => e.ProxyId == proxy.Id && e.Host == "1.1.1.1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerAsync_Should_DoNothing_When_ProviderAccountIsMissing()
    {
        // Data-integrity edge case: proxy.ProviderAccountId doesn't resolve to any row (mirrors
        // the ManualProviderAccount well-known id used in production, but here deliberately left
        // unseeded). No host/provider context exists to act on, so this is a silent no-op —
        // the same guard-clause shape as the "proxy not found" case just above it in the service.
        await using var db = CreateDb();
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 1111, ProxyProtocol.Http, null, null, null);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();

        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        var outbox = Substitute.For<IOutboxWriter>();

        var sut = new ProxyRenewalService(db, factory, new FakeProtector(), outbox);

        await sut.TriggerAsync(proxy.Id, CancellationToken.None);

        await outbox.DidNotReceive().AddAsync(Arg.Any<ManualProxyNeedsAttentionIntegrationEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerAsync_Should_PublishNeedsAttentionEvent_When_RenewalAttemptFails()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "{}");
        var proxy = Proxy.Create(account.Id, "1.1.1.1", 1111, ProxyProtocol.Http, null, null, "ext-1");
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.SupportsRenew.Returns(true);
        adapter.RenewProxyAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<Proxy>(), Arg.Any<CancellationToken>())
            .Returns(ProviderRenewResult.Failed("provider rejected the rotation request"));
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.WebShare).Returns(adapter);
        var outbox = Substitute.For<IOutboxWriter>();

        var sut = new ProxyRenewalService(db, factory, new FakeProtector(), outbox);

        await sut.TriggerAsync(proxy.Id, CancellationToken.None);

        await outbox.Received(1).AddAsync(Arg.Any<ManualProxyNeedsAttentionIntegrationEvent>(), Arg.Any<CancellationToken>());
    }
}
