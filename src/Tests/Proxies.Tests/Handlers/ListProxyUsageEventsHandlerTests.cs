using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.UsageEvents;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.UsageEvents.ListProxyUsageEvents;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ListProxyUsageEventsHandlerTests
{
    private static ProxiesDbContext CreateDb() =>
        TestProxiesDbContext.Create(new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(ProxiesDbContext Db, Proxy Proxy, ApiClient Client)> SeedAsync()
    {
        var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
        var proxy = Proxy.Create(account.Id, "10.0.0.5", 3128, ProxyProtocol.Http, "brd-customer-ip-200.1.2.3", "protected:p", null);
        var client = ApiClient.Create("grabber", "hash");
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(proxy);
        db.ApiClients.Add(client);
        await db.SaveChangesAsync();
        return (db, proxy, client);
    }

    [Fact]
    public async Task Handle_Should_ReturnNewestFirst_WithReporterNameResolved()
    {
        var (db, proxy, client) = await SeedAsync();
        await using var _ = db;
        var older = ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Success, null, client.Id, null);
        var newer = ProxyUsageEvent.Create(proxy.Id, UsageEventSource.SystemHealthCheck, UsageEventOutcome.Timeout, null, null, "probe timed out");
        db.ProxyUsageEvents.AddRange(older, newer);
        await db.SaveChangesAsync();

        // The factory stamps OccurredAtUtc = UtcNow, so two events created in the same tick tie on
        // the sort key and fall through to the Id tiebreak — deterministic for paging, but not
        // chronological. Space them explicitly so "newest first" is what this test measures.
        db.Entry(older).Property(nameof(ProxyUsageEvent.OccurredAtUtc)).CurrentValue = DateTime.UtcNow.AddMinutes(-10);
        db.Entry(newer).Property(nameof(ProxyUsageEvent.OccurredAtUtc)).CurrentValue = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var sut = new ListProxyUsageEventsQueryHandler(db);

        var result = await sut.Handle(new ListProxyUsageEventsQuery(proxy.Id), CancellationToken.None);

        result.TotalCount.ShouldBe(2);
        var first = result.Items.First();
        first.Outcome.ShouldBe(UsageEventOutcome.Timeout);
        first.Source.ShouldBe(UsageEventSource.SystemHealthCheck);
        first.Detail.ShouldBe("probe timed out");
        first.ReportedByApiClientName.ShouldBeNull();
        first.ProxyHost.ShouldBe("10.0.0.5");
        first.ProxyPort.ShouldBe(3128);
        // Carried so the fleet-wide feed can identify a proxy by the IP providers put in the username.
        first.ProxyUsername.ShouldBe("brd-customer-ip-200.1.2.3");
        result.Items.Last().ReportedByApiClientName.ShouldBe("grabber");
    }

    [Fact]
    public async Task Handle_Should_FilterToFailuresOnly_MatchingThePolicyEngineDefinition()
    {
        var (db, proxy, client) = await SeedAsync();
        await using var _ = db;
        db.ProxyUsageEvents.AddRange(
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Success, null, client.Id, null),
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Failure, null, client.Id, null),
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, client.Id, null),
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Timeout, null, client.Id, null));
        await db.SaveChangesAsync();
        var sut = new ListProxyUsageEventsQueryHandler(db);

        var result = await sut.Handle(new ListProxyUsageEventsQuery(proxy.Id, FailuresOnly: true), CancellationToken.None);

        result.TotalCount.ShouldBe(3);
        result.Items.ShouldAllBe(e => e.Outcome != UsageEventOutcome.Success);
    }

    [Fact]
    public async Task Handle_Should_FilterByOutcomeSourceAndDateRange()
    {
        var (db, proxy, client) = await SeedAsync();
        await using var _ = db;
        db.ProxyUsageEvents.AddRange(
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, client.Id, null),
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.SystemHealthCheck, UsageEventOutcome.Banned, null, null, null),
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Failure, null, client.Id, null));
        await db.SaveChangesAsync();
        var sut = new ListProxyUsageEventsQueryHandler(db);

        var byOutcome = await sut.Handle(new ListProxyUsageEventsQuery(proxy.Id, Outcome: UsageEventOutcome.Banned), CancellationToken.None);
        byOutcome.TotalCount.ShouldBe(2);

        var bySource = await sut.Handle(
            new ListProxyUsageEventsQuery(proxy.Id, Source: UsageEventSource.SystemHealthCheck), CancellationToken.None);
        bySource.TotalCount.ShouldBe(1);

        var future = await sut.Handle(
            new ListProxyUsageEventsQuery(proxy.Id, FromUtc: DateTime.UtcNow.AddMinutes(5)), CancellationToken.None);
        future.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_Should_ScopeToOneProxy_When_ProxyIdGiven_AndSpanTheFleetOtherwise()
    {
        var (db, proxy, client) = await SeedAsync();
        await using var _ = db;
        var other = Proxy.Create(proxy.ProviderAccountId, "10.0.0.6", 3128, ProxyProtocol.Http, null, null, null);
        db.Proxies.Add(other);
        db.ProxyUsageEvents.AddRange(
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Failure, null, client.Id, null),
            ProxyUsageEvent.Create(other.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Failure, null, client.Id, null));
        await db.SaveChangesAsync();
        var sut = new ListProxyUsageEventsQueryHandler(db);

        (await sut.Handle(new ListProxyUsageEventsQuery(proxy.Id), CancellationToken.None)).TotalCount.ShouldBe(1);
        (await sut.Handle(new ListProxyUsageEventsQuery(), CancellationToken.None)).TotalCount.ShouldBe(2);
    }

    [Fact]
    public async Task Handle_Should_ReturnNullUsername_When_ProxyHasNoAuth()
    {
        var (db, proxy, _) = await SeedAsync();
        await using var _ = db;
        var noAuth = Proxy.Create(proxy.ProviderAccountId, "10.0.0.9", 3128, ProxyProtocol.Http, null, null, null);
        db.Proxies.Add(noAuth);
        db.ProxyUsageEvents.Add(ProxyUsageEvent.Create(
            noAuth.Id, UsageEventSource.SystemHealthCheck, UsageEventOutcome.Success, null, null, null));
        await db.SaveChangesAsync();
        var sut = new ListProxyUsageEventsQueryHandler(db);

        var result = await sut.Handle(new ListProxyUsageEventsQuery(noAuth.Id), CancellationToken.None);

        result.Items.Single().ProxyUsername.ShouldBeNull();
        result.Items.Single().ProxyHost.ShouldBe("10.0.0.9");
    }

    [Fact]
    public async Task Handle_Should_KeepEvents_When_ReporterApiClientNoLongerExists()
    {
        // The reporter is stored as a bare id, not an FK-with-cascade, so a deleted API client
        // must leave its events in the timeline (unnamed) rather than drop them.
        var (db, proxy, _) = await SeedAsync();
        await using var __ = db;
        db.ProxyUsageEvents.Add(ProxyUsageEvent.Create(
            proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Failure, null, Guid.NewGuid(), "gone"));
        await db.SaveChangesAsync();
        var sut = new ListProxyUsageEventsQueryHandler(db);

        var result = await sut.Handle(new ListProxyUsageEventsQuery(proxy.Id), CancellationToken.None);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().ReportedByApiClientName.ShouldBeNull();
        result.Items.Single().ReportedByApiClientId.ShouldNotBeNull();
    }
}
