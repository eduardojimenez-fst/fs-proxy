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

    // Fix round 3 (Item 2): ProxySnapshot must copy even when handed a ProxyEndpoint[] directly, not
    // just a List<ProxyEndpoint>. The coordinator's original suggested fix ("endpoints as
    // ProxyEndpoint[] ?? endpoints.ToArray()") would have skipped the defensive copy for exactly this
    // shape — and a bare array is one of the most natural things for a custom
    // IProxyServiceClient/IProxySnapshotCache to hand back (e.g. straight out of
    // JsonSerializer.Deserialize<ProxyEndpoint[]>). Requires InternalsVisibleTo(Proxy.Client.Tests)
    // on FS.Proxy.Client, since ProxySnapshot is internal — ProxyPool's own public surface has no way
    // to observe this invariant directly.
    [Fact]
    public void ProxySnapshot_Should_Not_Be_Affected_By_Mutating_The_Original_Array_Afterward()
    {
        ProxyEndpoint[] original = [.. Endpoints(2)];
        Guid originalFirstId = original[0].Id;

        var snapshot = new ProxySnapshot(original, DateTimeOffset.UtcNow);

        // Mutate the ORIGINAL array's slot (not the immutable ProxyEndpoint instance itself — every
        // ProxyEndpoint property is get-only) after the snapshot was built.
        original[0] = new ProxyEndpoint(Guid.NewGuid(), "replaced", 9999, ProxyProtocol.Http, "x", "y");

        // If the constructor had aliased the array instead of copying it, this would now read the
        // replacement's id instead.
        snapshot.Endpoints[0].Id.ShouldBe(originalFirstId);
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

        // Fix round 3: advance the clock BEFORE the failing refresh, well short of StaleCeiling.
        // Without this, the refresh runs at the same instant as the warmup, so a regression that
        // re-stamps FetchedAt = _clock() during an empty refresh would compute the exact same value
        // as the untouched warmup timestamp — making the assertions below pass under both the
        // correct code and the precise bug they exist to catch.
        clock.Now += TimeSpan.FromMinutes(5);

        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProxyEndpoint>>([]));

        await pool.RefreshAsync(CancellationToken.None);
        var result = pool.Next();

        result.ShouldNotBeNull();
        endpoints.Select(e => e.Id).ShouldContain(result!.Id);

        // An empty refresh must not re-stamp FetchedAt. Advance a further 5 minutes + 1 second: the
        // total from the ORIGINAL warmup instant is 10 minutes + 1 second (just past the 10-minute
        // StaleCeiling), but the total from the refresh instant is only 5 minutes + 1 second (well
        // under it). So this is only stale if FetchedAt is still the warmup timestamp — a bug that
        // bumped FetchedAt during the empty refresh would make both assertions below fail instead of
        // passing regardless, which is what fix round 2's version of this test could not do.
        clock.Now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
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

        // Fix round 3: same reasoning as the empty-refresh test above — advance before the failing
        // refresh so a re-stamped FetchedAt would produce a distinguishable timestamp.
        clock.Now += TimeSpan.FromMinutes(5);

        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ProxyEndpoint>>(new HttpRequestException("boom")));

        await Should.NotThrowAsync(() => pool.RefreshAsync(CancellationToken.None));
        var result = pool.Next();

        result.ShouldNotBeNull();
        endpoints.Select(e => e.Id).ShouldContain(result!.Id);

        // Same FetchedAt pin as the empty-result case above, for the transport-throws path: total
        // from warmup is 10 minutes + 1 second (past StaleCeiling); total from the refresh instant
        // is only 5 minutes + 1 second (well under it) — distinguishing a correct "FetchedAt
        // untouched" from a regressed "FetchedAt re-stamped on throw."
        clock.Now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
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

    // Task 8 add-on: ProxyPool.Endpoints (internal) is what ProxySource.GetProxies reads to hand a
    // caller the pool's whole membership at once — not one of the 11 numbered behaviors (this member
    // didn't exist until Task 8 needed it), but it follows the exact same degrade rule as Next()/
    // HealthyCount, so it gets the same two-sided coverage they got.
    [Fact]
    public async Task Endpoints_Should_Return_The_Current_Snapshots_Proxies()
    {
        var endpoints = Endpoints(3);
        var pool = await WarmedPool(endpoints);

        pool.Endpoints.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Select(e => e.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task Endpoints_Should_Be_Empty_Once_The_Snapshot_Is_Stale()
    {
        var endpoints = Endpoints(2);
        var clock = new FakeClock();
        var options = Options();
        options.StaleCeiling = TimeSpan.FromMinutes(10);
        var pool = await WarmedPool(endpoints, clock.Get, options);

        clock.Now += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);

        pool.Endpoints.ShouldBeEmpty();
    }

    // Fix round 1 (Important 3): Endpoints must filter out quarantined proxies, mirroring Next()'s own
    // rule exactly — this is what makes ProxySource.Report's local quarantine visible through
    // GetProxies, not just Lease. Without this, a Level-0 (.NET Framework 4.8) caller whose only
    // surface is GetProxies would never see Report's local half do anything at all.
    [Fact]
    public async Task Endpoints_Should_Exclude_A_Quarantined_Proxy()
    {
        var endpoints = Endpoints(3);
        var pool = await WarmedPool(endpoints);

        pool.Quarantine(endpoints[0].Id);

        pool.Endpoints.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Skip(1).Select(e => e.Id)).ShouldBeTrue();
    }

    // Companion: when EVERY proxy is quarantined, Endpoints must fall back to the whole set rather
    // than an empty one — the same "a questionable proxy beats nothing at all" rule Next() already
    // applies when every candidate it would otherwise skip is quarantined.
    [Fact]
    public async Task Endpoints_Should_Return_The_Full_Set_When_Every_Proxy_Is_Quarantined()
    {
        var endpoints = Endpoints(2);
        var pool = await WarmedPool(endpoints);

        foreach (ProxyEndpoint endpoint in endpoints)
        {
            pool.Quarantine(endpoint.Id);
        }

        pool.Endpoints.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Select(e => e.Id)).ShouldBeTrue();
    }

    // Fix round 1 fold-in: Endpoints must not hand back something a caller can downcast to a mutable
    // array/list and use to corrupt the live snapshot for every other reader. Task 6 spent a whole fix
    // round enforcing this on the way IN to ProxySnapshot (ProxySnapshot_Should_Not_Be_Affected_By...
    // above); this pins the same invariant on the way OUT, through the one member that hands the
    // snapshot's contents out as more than one ProxyEndpoint at a time.
    [Fact]
    public async Task Endpoints_Should_Not_Be_Downcastable_To_A_Mutable_Collection()
    {
        var endpoints = Endpoints(3);
        var pool = await WarmedPool(endpoints);

        IReadOnlyList<ProxyEndpoint> result = pool.Endpoints;

        (result is ProxyEndpoint[]).ShouldBeFalse("a caller must not be able to downcast this to the live snapshot's backing array.");
        (result is List<ProxyEndpoint>).ShouldBeFalse("a caller must not be able to downcast this to a mutable list either.");
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
