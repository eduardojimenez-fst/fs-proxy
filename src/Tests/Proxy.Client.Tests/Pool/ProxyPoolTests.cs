using System.Net.Http;
using FSH.Proxy.Client;
using FSH.Proxy.Client.Caching;
using FSH.Proxy.Client.Pool;
using FSH.Proxy.Client.Transport;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests.Pool;

public sealed class ProxyPoolTests
{
    private static ProxyClientOptions Options(IProxySnapshotCache? cache = null) => new()
    {
        BaseAddress = new Uri("https://proxy.test"),
        ApiKey = "key",
        PoolSize = 10,
        StaleCeiling = TimeSpan.FromMinutes(10),
        Quarantine = TimeSpan.FromMinutes(2),
        SnapshotCache = cache,
    };

    private static List<ProxyEndpoint> Endpoints(int count)
    {
        var list = new List<ProxyEndpoint>();
        for (int i = 0; i < count; i++)
        {
            list.Add(new ProxyEndpoint(Guid.NewGuid(), "203.0.113." + i, 8080, ProxyProtocol.Http, "u", "p"));
        }
        return list;
    }

    private static IProxyServiceClient ClientReturning(IReadOnlyList<ProxyEndpoint> endpoints)
    {
        var client = Substitute.For<IProxyServiceClient>();
        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProxyEndpoint>>(endpoints));
        return client;
    }

    private static async Task<ProxyPool> WarmedPool(
        IReadOnlyList<ProxyEndpoint> endpoints, Func<DateTimeOffset>? clock = null, ProxyClientOptions? options = null)
    {
        var client = ClientReturning(endpoints);
        var pool = new ProxyPool(client, options ?? Options(), ["country:cl"], clock);
        await pool.WarmupAsync(CancellationToken.None);
        return pool;
    }

    /// <summary>A clock the test controls directly, so quarantine expiry and staleness never need Thread.Sleep.</summary>
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Get() => Now;
    }

    // 1. WarmupAsync fills from the service and Next() then returns a proxy.
    [Fact]
    public async Task WarmupAsync_Should_Fill_From_The_Service_So_Next_Returns_A_Proxy()
    {
        var endpoints = Endpoints(3);
        var client = ClientReturning(endpoints);
        var pool = new ProxyPool(client, Options(), ["country:cl"]);

        await pool.WarmupAsync(CancellationToken.None);
        var result = pool.Next();

        result.ShouldNotBeNull();
        endpoints.Select(e => e.Id).ShouldContain(result!.Id);
    }

    // Fix round 2 (Important 3): "clamps count to PoolSize" and "requests for the pool's own tags"
    // had no dedicated assertion — ClientReturning's setup matches Arg.Any<int>()/
    // Arg.Any<IReadOnlyList<string>>(), so a hardcoded request count or the wrong tag list would
    // have passed every other test in this file.
    [Fact]
    public async Task WarmupAsync_Should_Request_The_Pools_Tags_And_PoolSize()
    {
        List<string> tags = ["country:cl", "entitytype:tender"];
        var options = Options();
        options.PoolSize = 7;
        var client = ClientReturning(Endpoints(1));
        var pool = new ProxyPool(client, options, tags);

        await pool.WarmupAsync(CancellationToken.None);

        // NSubstitute's Received() assertion is synchronous — it only inspects the call history —
        // so the Task it hands back here is never meant to be awaited; discard it explicitly rather
        // than triggering CS4014 ("this call is not awaited").
        _ = client.Received(1).RequestAsync(
            Arg.Is<IReadOnlyList<string>>(sent => sent.SequenceEqual(tags)),
            7,
            Arg.Any<CancellationToken>());
    }

    // 2. Next() rotates: over N calls against a pool of N, every proxy is returned exactly once.
    [Fact]
    public async Task Next_Should_Rotate_So_Every_Proxy_Is_Returned_Exactly_Once_Over_N_Calls()
    {
        var endpoints = Endpoints(4);
        var pool = await WarmedPool(endpoints);

        var seen = new List<Guid>();
        for (int i = 0; i < endpoints.Count; i++)
        {
            seen.Add(pool.Next()!.Id);
        }

        seen.Distinct().Count().ShouldBe(endpoints.Count);
        seen.ToHashSet().SetEquals(endpoints.Select(e => e.Id)).ShouldBeTrue();
    }

    // 3. A quarantined proxy is not returned while the cooldown is live.
    [Fact]
    public async Task Next_Should_Not_Return_A_Quarantined_Proxy_While_The_Cooldown_Is_Live()
    {
        var endpoints = Endpoints(3);
        var clock = new FakeClock();
        var pool = await WarmedPool(endpoints, clock.Get);

        pool.Quarantine(endpoints[0].Id);

        for (int i = 0; i < 10; i++)
        {
            pool.Next()!.Id.ShouldNotBe(endpoints[0].Id);
        }
    }

    // 4. Once the cooldown expires (advance the clock), it is returned again.
    [Fact]
    public async Task Next_Should_Return_A_Quarantined_Proxy_Again_Once_The_Cooldown_Expires()
    {
        var endpoints = Endpoints(2);
        var clock = new FakeClock();
        var options = Options();
        options.Quarantine = TimeSpan.FromMinutes(2);
        var pool = await WarmedPool(endpoints, clock.Get, options);

        pool.Quarantine(endpoints[0].Id);
        clock.Now += TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1);

        var seen = new HashSet<Guid>();
        for (int i = 0; i < 4; i++)
        {
            seen.Add(pool.Next()!.Id);
        }

        seen.ShouldContain(endpoints[0].Id);
    }

    // 5. With every proxy quarantined, Next() still returns one rather than null.
    [Fact]
    public async Task Next_Should_Return_A_Proxy_Even_When_Every_Proxy_Is_Quarantined()
    {
        var endpoints = Endpoints(3);
        var pool = await WarmedPool(endpoints);

        foreach (var endpoint in endpoints)
        {
            pool.Quarantine(endpoint.Id);
        }

        var result = pool.Next();

        result.ShouldNotBeNull();
        endpoints.Select(e => e.Id).ShouldContain(result!.Id);
    }

    // 6. When RefreshAsync gets an empty list (service 404), the previous snapshot keeps serving.
    [Fact]
    public async Task RefreshAsync_Should_Keep_Serving_The_Previous_Snapshot_When_The_Service_Returns_Empty()
    {
        var endpoints = Endpoints(2);
        var clock = new FakeClock();
        var options = Options();
        options.StaleCeiling = TimeSpan.FromMinutes(10);
        var client = ClientReturning(endpoints);
        var pool = new ProxyPool(client, options, ["country:cl"], clock.Get);
        await pool.WarmupAsync(CancellationToken.None);

        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProxyEndpoint>>([]));

        await pool.RefreshAsync(CancellationToken.None);
        var result = pool.Next();

        result.ShouldNotBeNull();
        endpoints.Select(e => e.Id).ShouldContain(result!.Id);

        // Fix round 2: an empty refresh must not re-stamp FetchedAt. Advancing the clock past
        // StaleCeiling, measured from the ORIGINAL warmup instant, must still make the snapshot
        // stale — if a future edit bumped FetchedAt on an empty fetch, a pool whose service has been
        // down for hours would never go stale and would keep handing out long-retired proxies past
        // StaleCeiling forever. The two assertions above would pass either way; only this one pins it.
        clock.Now += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);
        pool.IsStale.ShouldBeTrue();
        pool.Next().ShouldBeNull();
    }

    // Extra: the same degrade-don't-fail contract applies when the transport throws outright, not
    // only when it answers with an empty list. Not one of the 11 numbered behaviors, but "catches
    // ALL exceptions... on failure leaves the snapshot in place" is stated explicitly as a
    // requirement, so it gets its own assertion.
    [Fact]
    public async Task RefreshAsync_Should_Keep_Serving_The_Previous_Snapshot_When_The_Transport_Throws()
    {
        var endpoints = Endpoints(2);
        var clock = new FakeClock();
        var options = Options();
        options.StaleCeiling = TimeSpan.FromMinutes(10);
        var client = ClientReturning(endpoints);
        var pool = new ProxyPool(client, options, ["country:cl"], clock.Get);
        await pool.WarmupAsync(CancellationToken.None);

        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ProxyEndpoint>>(new HttpRequestException("boom")));

        await Should.NotThrowAsync(() => pool.RefreshAsync(CancellationToken.None));
        var result = pool.Next();

        result.ShouldNotBeNull();
        endpoints.Select(e => e.Id).ShouldContain(result!.Id);

        // Fix round 2: same FetchedAt pin as the empty-result case above, for the transport-throws
        // path — a failed refresh must not reset the staleness clock either.
        clock.Now += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);
        pool.IsStale.ShouldBeTrue();
        pool.Next().ShouldBeNull();
    }

    // 7. Past StaleCeiling, IsStale is true and Next() returns null.
    [Fact]
    public async Task Next_Should_Return_Null_And_IsStale_Should_Be_True_Past_The_Stale_Ceiling()
    {
        var endpoints = Endpoints(2);
        var clock = new FakeClock();
        var options = Options();
        options.StaleCeiling = TimeSpan.FromMinutes(10);
        var pool = await WarmedPool(endpoints, clock.Get, options);

        clock.Now += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);

        pool.IsStale.ShouldBeTrue();
        pool.Next().ShouldBeNull();
    }

    // 8. A successful refresh clears staleness and replaces the snapshot wholesale.
    [Fact]
    public async Task RefreshAsync_Should_Clear_Staleness_And_Replace_The_Snapshot_Wholesale()
    {
        var initial = Endpoints(2);
        var clock = new FakeClock();
        var options = Options();
        options.StaleCeiling = TimeSpan.FromMinutes(10);
        var client = ClientReturning(initial);
        var pool = new ProxyPool(client, options, ["country:cl"], clock.Get);
        await pool.WarmupAsync(CancellationToken.None);

        clock.Now += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);
        pool.IsStale.ShouldBeTrue();

        var fresh = Endpoints(2);
        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProxyEndpoint>>(fresh));

        await pool.RefreshAsync(CancellationToken.None);

        pool.IsStale.ShouldBeFalse();
        var seen = new HashSet<Guid>();
        for (int i = 0; i < 4; i++)
        {
            seen.Add(pool.Next()!.Id);
        }
        seen.SetEquals(fresh.Select(e => e.Id)).ShouldBeTrue();
    }

    // 9. WarmupAsync falls back to the cache when the service throws, and Next() serves the cached proxies.
    [Fact]
    public async Task WarmupAsync_Should_Fall_Back_To_The_Cache_When_The_Service_Throws()
    {
        var cached = Endpoints(2);
        var cache = Substitute.For<IProxySnapshotCache>();
        cache.Read(Arg.Any<string>()).Returns(cached);
        var client = Substitute.For<IProxyServiceClient>();
        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ProxyEndpoint>>(new HttpRequestException("boom")));
        var pool = new ProxyPool(client, Options(cache), ["country:cl"]);

        await pool.WarmupAsync(CancellationToken.None);
        var result = pool.Next();

        result.ShouldNotBeNull();
        cached.Select(e => e.Id).ShouldContain(result!.Id);
    }

    // 10. WarmupAsync writes a successful fetch to the cache.
    [Fact]
    public async Task WarmupAsync_Should_Write_A_Successful_Fetch_To_The_Cache()
    {
        var endpoints = Endpoints(2);
        var cache = Substitute.For<IProxySnapshotCache>();
        var client = ClientReturning(endpoints);
        var pool = new ProxyPool(client, Options(cache), ["country:cl"]);

        await pool.WarmupAsync(CancellationToken.None);

        cache.Received(1).Write(
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<ProxyEndpoint>>(list => list.Select(e => e.Id).SequenceEqual(endpoints.Select(e => e.Id))));
    }

    // Fix round 1: the write-through to the cache is not limited to WarmupAsync — a RefreshAsync
    // that is NOT a warmup must also write its successful fetch, or a long-running process's on-disk
    // fallback goes stale (it would forever reflect only the very first fetch at process start).
    [Fact]
    public async Task RefreshAsync_Should_Write_A_Successful_Fetch_To_The_Cache()
    {
        var initial = Endpoints(2);
        var cache = Substitute.For<IProxySnapshotCache>();
        var client = ClientReturning(initial);
        var pool = new ProxyPool(client, Options(cache), ["country:cl"]);
        await pool.WarmupAsync(CancellationToken.None);
        cache.ClearReceivedCalls();

        var refreshed = Endpoints(2);
        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProxyEndpoint>>(refreshed));
        await pool.RefreshAsync(CancellationToken.None);

        cache.Received(1).Write(
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<ProxyEndpoint>>(list => list.Select(e => e.Id).SequenceEqual(refreshed.Select(e => e.Id))));
    }

    // Fix round 1, the half most likely to regress silently: an empty refresh result must NOT
    // overwrite what is already on disk. A stale-but-parseable cached snapshot looks exactly like a
    // fresh one to Read's TTL check, so overwriting a good cache entry with "nothing" here would
    // quietly destroy the fallback the cache exists to provide.
    [Fact]
    public async Task RefreshAsync_Should_Not_Overwrite_The_Cache_When_The_Service_Returns_Empty()
    {
        var initial = Endpoints(2);
        var cache = Substitute.For<IProxySnapshotCache>();
        var client = ClientReturning(initial);
        var pool = new ProxyPool(client, Options(cache), ["country:cl"]);
        await pool.WarmupAsync(CancellationToken.None);
        cache.ClearReceivedCalls();

        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProxyEndpoint>>([]));
        await pool.RefreshAsync(CancellationToken.None);

        cache.DidNotReceive().Write(Arg.Any<string>(), Arg.Any<IReadOnlyList<ProxyEndpoint>>());
    }

    // 11. Refresh replaces the snapshot atomically — a proxy removed server-side stops being returned.
    [Fact]
    public async Task RefreshAsync_Should_Stop_Returning_A_Proxy_Removed_Server_Side()
    {
        var endpoints = Endpoints(2);
        var client = ClientReturning(endpoints);
        var pool = new ProxyPool(client, Options(), ["country:cl"]);
        await pool.WarmupAsync(CancellationToken.None);

        var survivor = endpoints[0];
        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProxyEndpoint>>([survivor]));
        await pool.RefreshAsync(CancellationToken.None);

        for (int i = 0; i < 6; i++)
        {
            pool.Next()!.Id.ShouldBe(survivor.Id);
        }
    }

    // Extra, not one of the 11: HealthyCount is part of the required public surface
    // (`Produces: ... int HealthyCount ...`) and nothing above exercises it directly.
    [Fact]
    public async Task HealthyCount_Should_Exclude_Quarantined_Proxies()
    {
        var endpoints = Endpoints(3);
        var pool = await WarmedPool(endpoints);

        pool.Quarantine(endpoints[0].Id);

        pool.HealthyCount.ShouldBe(2);
    }

    // Fix round 2 (Important 2): HealthyCount must not report proxies as healthy once the snapshot
    // itself is stale. Next() already refuses to serve a stale snapshot; a caller gating an
    // out-of-band refresh on HealthyCount > 0 (HealthyCount's own documented purpose) would
    // otherwise see a healthy-looking count for a pool that Next() is silently starving, and never
    // trigger the recovery it exists to trigger.
    [Fact]
    public async Task HealthyCount_Should_Be_Zero_Once_The_Snapshot_Is_Stale()
    {
        var endpoints = Endpoints(3);
        var clock = new FakeClock();
        var options = Options();
        options.StaleCeiling = TimeSpan.FromMinutes(10);
        var pool = await WarmedPool(endpoints, clock.Get, options);

        clock.Now += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);

        pool.HealthyCount.ShouldBe(0);
    }

    // Fix round 2 (Important 4): a proxy's local quarantine must not survive it being retired
    // server-side. Without pruning, this id would sit in the quarantine dictionary for the rest of
    // the process's life once it leaves every future snapshot — and if the service ever reissues the
    // same id later, it would incorrectly still read as quarantined until the ORIGINAL cooldown
    // elapsed, no matter how much later it actually reappeared. Observed here via HealthyCount: if
    // pruning didn't run when the proxy was retired, its quarantine entry would still be present
    // (and still live, since the 2-minute cooldown has not elapsed) when it reappears.
    [Fact]
    public async Task Refresh_Should_Drop_Quarantine_For_A_Proxy_Retired_Server_Side()
    {
        var endpoints = Endpoints(2);
        var quarantined = endpoints[0];
        var survivor = endpoints[1];
        var clock = new FakeClock();
        var options = Options();
        options.Quarantine = TimeSpan.FromMinutes(2);
        var client = ClientReturning(endpoints);
        var pool = new ProxyPool(client, options, ["country:cl"], clock.Get);
        await pool.WarmupAsync(CancellationToken.None);

        pool.Quarantine(quarantined.Id);
        pool.HealthyCount.ShouldBe(1);

        // Retire the quarantined proxy server-side: the next fetch no longer includes it.
        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProxyEndpoint>>([survivor]));
        await pool.RefreshAsync(CancellationToken.None);

        // The service reissues the SAME id — still well within the original 2-minute cooldown.
        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProxyEndpoint>>([quarantined, survivor]));
        await pool.RefreshAsync(CancellationToken.None);

        // Had the quarantine entry survived the retirement, this would still be 1 until clock.Now
        // passed the ORIGINAL cooldown — it has not been advanced at all in this test.
        pool.HealthyCount.ShouldBe(2);
    }
}
