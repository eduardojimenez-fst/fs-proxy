using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Proxies.ListProxies;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ListProxiesHandlerTests
{
    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Handle_Should_FilterByTag()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
        var tag = Tag.Create("pais:cl");
        var matching = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        matching.AssignTag(tag.Id);
        var other = Proxy.Create(account.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        db.ProviderAccounts.Add(account);
        db.Tags.Add(tag);
        db.Proxies.AddRange(matching, other);
        await db.SaveChangesAsync();
        var sut = new ListProxiesQueryHandler(db);

        var result = await sut.Handle(new ListProxiesQuery(["pais:cl"], null, null), CancellationToken.None);

        result.Items.Select(x => x.Id).ShouldBe([matching.Id]);
        result.Items.Single().Tags.ShouldBe(["pais:cl"]);
    }

    [Fact]
    public async Task Handle_Should_FilterByStatus()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
        var active = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        active.SetStatus(ProxyStatus.Active);
        var disabled = Proxy.Create(account.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        disabled.SetStatus(ProxyStatus.Disabled);
        db.ProviderAccounts.Add(account);
        db.Proxies.AddRange(active, disabled);
        await db.SaveChangesAsync();
        var sut = new ListProxiesQueryHandler(db);

        var result = await sut.Handle(new ListProxiesQuery(null, ProxyStatus.Active, null), CancellationToken.None);

        result.Items.Select(x => x.Id).ShouldBe([active.Id]);
    }

    [Fact]
    public async Task Handle_Should_FilterByGeolocation_CaseInsensitively()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
        var chile = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null, "cl");
        var argentina = Proxy.Create(account.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null, "AR");
        db.ProviderAccounts.Add(account);
        db.Proxies.AddRange(chile, argentina);
        await db.SaveChangesAsync();
        var sut = new ListProxiesQueryHandler(db);

        var result = await sut.Handle(new ListProxiesQuery(null, null, null, "CL"), CancellationToken.None);

        result.Items.Select(x => x.Id).ShouldBe([chile.Id]);
        result.Items.Single().Geolocation.ShouldBe("cl");
    }

    [Fact]
    public async Task Handle_Should_FilterByKind()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
        var dataCenter = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null,
            geolocation: null, providerGrouping: null, kind: ProxyKind.DataCenter);
        var residential = Proxy.Create(account.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null,
            geolocation: null, providerGrouping: null, kind: ProxyKind.Residential);
        db.ProviderAccounts.Add(account);
        db.Proxies.AddRange(dataCenter, residential);
        await db.SaveChangesAsync();
        var sut = new ListProxiesQueryHandler(db);

        var result = await sut.Handle(new ListProxiesQuery(null, null, null, Kind: ProxyKind.DataCenter), CancellationToken.None);

        result.Items.Select(x => x.Id).ShouldBe([dataCenter.Id]);
        result.Items.Single().Kind.ShouldBe(ProxyKind.DataCenter);
    }

    [Fact]
    public async Task Handle_Should_FilterByHost_PartiallyAndCaseInsensitively()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
        var matching = Proxy.Create(account.Id, "gate.Example.com", 80, ProxyProtocol.Http, null, null, null);
        var other = Proxy.Create(account.Id, "10.0.0.5", 80, ProxyProtocol.Http, null, null, null);
        db.ProviderAccounts.Add(account);
        db.Proxies.AddRange(matching, other);
        await db.SaveChangesAsync();
        var sut = new ListProxiesQueryHandler(db);

        var result = await sut.Handle(new ListProxiesQuery(null, null, null, Host: "example"), CancellationToken.None);

        result.Items.Select(x => x.Id).ShouldBe([matching.Id]);
    }

    [Fact]
    public async Task Handle_Should_FilterByUsername_PartiallyAndCaseInsensitively()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
        var matching = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, "User-200.1.2.3", "protected:p", null);
        var other = Proxy.Create(account.Id, "2.2.2.2", 80, ProxyProtocol.Http, "user-190.9.9.9", "protected:p", null);
        var noAuth = Proxy.Create(account.Id, "3.3.3.3", 80, ProxyProtocol.Http, null, null, null);
        db.ProviderAccounts.Add(account);
        db.Proxies.AddRange(matching, other, noAuth);
        await db.SaveChangesAsync();
        var sut = new ListProxiesQueryHandler(db);

        var result = await sut.Handle(new ListProxiesQuery(null, null, null, Username: "200.1"), CancellationToken.None);

        result.Items.Select(x => x.Id).ShouldBe([matching.Id]);
    }

    [Fact]
    public async Task Handle_Should_Aggregate24hHealthCounters_IgnoringOlderEvents()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
        var proxy = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        var quiet = Proxy.Create(account.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        db.ProviderAccounts.Add(account);
        db.Proxies.AddRange(proxy, quiet);
        // Age THIS event — held by reference — past the 24h window by writing OccurredAtUtc
        // directly (the factory stamps UtcNow). Picking it with an unordered First() would make
        // the assertions below depend on which event the provider happened to return.
        var stale = ProxyUsageEvent.Create(proxy.Id, UsageEventSource.SystemHealthCheck, UsageEventOutcome.Success, null, null, null);
        db.ProxyUsageEvents.AddRange(
            stale,
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Success, null, null, null),
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Timeout, null, null, null),
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, null, null));
        await db.SaveChangesAsync();

        db.Entry(stale).Property(nameof(ProxyUsageEvent.OccurredAtUtc)).CurrentValue = DateTime.UtcNow.AddHours(-25);
        await db.SaveChangesAsync();

        var sut = new ListProxiesQueryHandler(db);
        var result = await sut.Handle(new ListProxiesQuery(null, null, null), CancellationToken.None);

        var busy = result.Items.Single(x => x.Id == proxy.Id);
        busy.SuccessCount24h.ShouldBe(1);
        busy.FailureCount24h.ShouldBe(2);
        busy.LastEventAtUtc.ShouldNotBeNull();

        // No events at all is distinct from "all successful" — both counters stay zero.
        var idle = result.Items.Single(x => x.Id == quiet.Id);
        idle.SuccessCount24h.ShouldBe(0);
        idle.FailureCount24h.ShouldBe(0);
        idle.LastEventAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_Should_ExposeUsername_SoOperatorsCanIdentifyTheProxy()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
        var withAuth = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, "200.1.2.3", "protected:p", null);
        var withoutAuth = Proxy.Create(account.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        db.ProviderAccounts.Add(account);
        db.Proxies.AddRange(withAuth, withoutAuth);
        await db.SaveChangesAsync();
        var sut = new ListProxiesQueryHandler(db);

        var result = await sut.Handle(new ListProxiesQuery(null, null, null), CancellationToken.None);

        result.Items.Single(x => x.Id == withAuth.Id).Username.ShouldBe("200.1.2.3");
        result.Items.Single(x => x.Id == withoutAuth.Id).Username.ShouldBeNull();
    }
}
