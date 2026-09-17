using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Jobs;
using FSH.Modules.Proxies.Options;
using Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Integration.Tests.Tests.Proxies;

/// <summary>
/// Cover for the daily usage-event retention job. This lives in the integration suite rather than
/// the unit suite because the job deletes through <c>ExecuteDeleteAsync</c>, which the EF Core
/// in-memory provider does not implement — a unit test would exercise nothing.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PurgeProxyUsageEventsJobTests
{
    private readonly FshWebApplicationFactory _factory;

    public PurgeProxyUsageEventsJobTests(FshWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task RunAsync_Should_DeleteEventsPastRetention_AndKeepRecentOnes()
    {
        var proxyId = await SeedProxyAsync();
        const int retentionDays = 30;

        var (staleIds, freshIds) = await SeedEventsAsync(
            proxyId,
            staleAges: [TimeSpan.FromDays(retentionDays + 1), TimeSpan.FromDays(365)],
            freshAges: [TimeSpan.Zero, TimeSpan.FromDays(retentionDays - 1)]);

        await RunJobAsync(retentionDays);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxiesDbContext>();
        (await db.ProxyUsageEvents.Where(e => staleIds.Contains(e.Id)).CountAsync())
            .ShouldBe(0, "events older than the retention window must be purged");
        (await db.ProxyUsageEvents.Where(e => freshIds.Contains(e.Id)).CountAsync())
            .ShouldBe(freshIds.Count, "events inside the retention window must survive");
    }

    [Fact]
    public async Task RunAsync_Should_BeIdempotent_And_DeleteNothing_When_AllEventsAreRecent()
    {
        var proxyId = await SeedProxyAsync();
        var (_, freshIds) = await SeedEventsAsync(proxyId, staleAges: [], freshAges: [TimeSpan.FromDays(1)]);

        await RunJobAsync(retentionDays: 30);
        await RunJobAsync(retentionDays: 30);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxiesDbContext>();
        (await db.ProxyUsageEvents.Where(e => freshIds.Contains(e.Id)).CountAsync()).ShouldBe(freshIds.Count);
    }

    [Fact]
    public async Task RunAsync_Should_HonourAShorterConfiguredRetention()
    {
        var proxyId = await SeedProxyAsync();
        var (_, ids) = await SeedEventsAsync(proxyId, staleAges: [], freshAges: [TimeSpan.FromDays(3)]);

        // Same rows, a tighter window: what survived above must now be purged.
        await RunJobAsync(retentionDays: 1);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxiesDbContext>();
        (await db.ProxyUsageEvents.Where(e => ids.Contains(e.Id)).CountAsync()).ShouldBe(0);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private async Task RunJobAsync(int retentionDays)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxiesDbContext>();
        var job = new PurgeProxyUsageEventsJob(
            db,
            Options.Create(new ProxiesOptions { UsageEventRetentionDays = retentionDays }),
            NullLogger<PurgeProxyUsageEventsJob>.Instance);
        await job.RunAsync(CancellationToken.None);
    }

    private async Task<Guid> SeedProxyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxiesDbContext>();
        var account = await db.ProviderAccounts.FirstAsync();
        // Unique host per call so parallel collections cannot collide; no randomness needed.
        var proxy = Proxy.Create(account.Id, $"purge-{Guid.NewGuid():N}.test", 3128, ProxyProtocol.Http, null, null, null);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        return proxy.Id;
    }

    /// <summary>
    /// Adds one event per supplied age. <c>ProxyUsageEvent.Create</c> stamps OccurredAtUtc = UtcNow,
    /// so the ages are applied with a direct property write afterwards.
    /// </summary>
    private async Task<(List<Guid> StaleIds, List<Guid> FreshIds)> SeedEventsAsync(
        Guid proxyId, TimeSpan[] staleAges, TimeSpan[] freshAges)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxiesDbContext>();

        List<Guid> stale = [];
        List<Guid> fresh = [];
        foreach (var (age, bucket) in staleAges.Select(a => (a, stale)).Concat(freshAges.Select(a => (a, fresh))))
        {
            var e = ProxyUsageEvent.Create(
                proxyId, UsageEventSource.SystemHealthCheck, UsageEventOutcome.Failure, null, null, null);
            db.ProxyUsageEvents.Add(e);
            await db.SaveChangesAsync();
            db.Entry(e).Property(nameof(ProxyUsageEvent.OccurredAtUtc)).CurrentValue = DateTime.UtcNow - age;
            await db.SaveChangesAsync();
            bucket.Add(e.Id);
        }
        return (stale, fresh);
    }
}
