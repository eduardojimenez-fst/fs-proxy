using FSH.Proxy.Client;
using FSH.Proxy.Client.Transport;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests;

public sealed class ProxySourceTests
{
    private static ProxyClientOptions Options() => new()
    {
        BaseAddress = new Uri("https://proxy.test"),
        ApiKey = "key",
        PoolSize = 10,
        StaleCeiling = TimeSpan.FromMinutes(10),
        Quarantine = TimeSpan.FromMinutes(2),
        FeedbackFlushInterval = TimeSpan.FromHours(1), // never fires on its own during a test
        RefreshInterval = TimeSpan.FromHours(1), // never fires on its own during a test
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
            .Returns(Task.FromResult(endpoints));
        return client;
    }

    /// <summary>A clock the test controls directly, so the reactive-refresh debounce never needs Thread.Sleep.</summary>
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Get() => Now;
    }

    // 1. GetProxies returns the warmed pool's contents.
    [Fact]
    public async Task GetProxies_Should_Return_The_Warmed_Pools_Contents()
    {
        var endpoints = Endpoints(3);
        var client = ClientReturning(endpoints);
        var options = Options();
        options.PoolSize = 3; // fully healthy at 3/3 — keeps this test's own concern isolated from reactive refresh
        options.Tags = ["country:cl"];
        await using var source = new ProxySource(options, client);

        await source.WarmupAsync();
        IReadOnlyList<ProxyEndpoint> result = source.GetProxies("country:cl");

        result.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Select(e => e.Id)).ShouldBeTrue();
    }

    // 2. GetProxies performs no I/O. Two complementary assertions, per fix round 1: a transport that
    // THROWS is swallowed by ProxyPool.RefreshAsync's own catch-all, so a regression that made
    // GetProxies dispatch (or even block on) a transport call could still pass a throw-only version of
    // this test. DidNotReceive is what actually pins "no I/O" — it fails on a transport TOUCH, not
    // merely a transport error.
    [Fact]
    public async Task GetProxies_Should_Perform_No_IO_When_The_Pool_Is_Healthy()
    {
        var endpoints = Endpoints(2);
        var client = ClientReturning(endpoints);
        var options = Options();
        options.PoolSize = 2; // HealthyCount(2) * 2 >= PoolSize(2): fully healthy, no reactive refresh
        options.Tags = ["country:cl"];
        await using var source = new ProxySource(options, client);
        await source.WarmupAsync();
        client.ClearReceivedCalls();

        // Defense in depth: if GetProxies ever did touch the transport, this makes it obvious via an
        // exception too — but DidNotReceive below is the assertion that actually proves the property.
        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ProxyEndpoint>>(new InvalidOperationException("GetProxies must not call the transport.")));

        IReadOnlyList<ProxyEndpoint> result = source.GetProxies("country:cl");

        result.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Select(e => e.Id)).ShouldBeTrue();
        _ = client.DidNotReceive().RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // Fix round 1, Important 5: the property under test is that GetProxies never BLOCKS on I/O, which
    // a throwing-transport test cannot demonstrate at all (nothing to block on if it throws instantly).
    // Here the pool is deliberately dropped below 50% healthy so the very next GetProxies call
    // dispatches a reactive refresh against a transport that never completes — proving the dispatch is
    // genuinely fire-and-forget, not awaited or blocked upon.
    [Fact]
    public async Task GetProxies_Should_Return_Immediately_Even_When_A_Dispatched_Refresh_Never_Completes()
    {
        var endpoints = Endpoints(2);
        var client = ClientReturning(endpoints);
        var options = Options();
        options.PoolSize = 2;
        options.Tags = ["country:cl"];
        await using var source = new ProxySource(options, client);
        await source.WarmupAsync();

        // Drop below the 50%-healthy threshold so the next GetProxies call dispatches a reactive
        // refresh — exactly the call this test needs to hang.
        foreach (ProxyEndpoint endpoint in endpoints)
        {
            source.Report(endpoint.Id, ProxyOutcome.Failure);
        }

        var neverCompletes = new TaskCompletionSource<IReadOnlyList<ProxyEndpoint>>();
        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(neverCompletes.Task);

        Task<IReadOnlyList<ProxyEndpoint>> getProxiesTask = Task.Run(() => source.GetProxies("country:cl"));
        Task firstCompleted = await Task.WhenAny(getProxiesTask, Task.Delay(TimeSpan.FromSeconds(5)));

        firstCompleted.ShouldBe(getProxiesTask, "GetProxies must return without waiting on a dispatched refresh, even one whose transport call never completes.");
    }

    // 3. Tag sets are normalized and order-insensitive: two calls with the same tags in different
    // order/casing hit the SAME pool, and the transport is called exactly once (during warmup) —
    // never once per call.
    [Fact]
    public async Task GetProxies_Should_Normalize_And_Sort_Tags_So_Order_And_Casing_Hit_The_Same_Pool()
    {
        var endpoints = Endpoints(2);
        var client = ClientReturning(endpoints);
        var options = Options();
        options.PoolSize = 2; // keeps this test's transport-call-count assertion isolated from reactive refresh
        options.Tags = ["country:cl", "entitytype:tender"];
        await using var source = new ProxySource(options, client);
        await source.WarmupAsync();

        IReadOnlyList<ProxyEndpoint> a = source.GetProxies("Country:CL", "entityType:Tender");
        IReadOnlyList<ProxyEndpoint> b = source.GetProxies("entitytype:tender", "country:cl");

        a.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Select(e => e.Id)).ShouldBeTrue();
        b.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Select(e => e.Id)).ShouldBeTrue();
        _ = client.Received(1).RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // Fix round 1, Important 4: GetProxies()/Lease() with no arguments must fall back to
    // ProxyClientOptions.Tags, not create a separate untagged pool.
    [Fact]
    public async Task GetProxies_With_No_Tags_Should_Fall_Back_To_The_Configured_Default_Tags()
    {
        var endpoints = Endpoints(2);
        var client = ClientReturning(endpoints);
        var options = Options();
        options.PoolSize = 2;
        options.Tags = ["country:cl"];
        await using var source = new ProxySource(options, client);
        await source.WarmupAsync();

        IReadOnlyList<ProxyEndpoint> result = source.GetProxies();

        result.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Select(e => e.Id)).ShouldBeTrue();
        // Must have hit the already-warmed default pool, not created a second, untagged one.
        _ = client.Received(1).RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // Companion to the above: Lease() shares the exact same EffectiveTags fallback.
    [Fact]
    public async Task Lease_With_No_Tags_Should_Fall_Back_To_The_Configured_Default_Tags()
    {
        var endpoints = Endpoints(2);
        var client = ClientReturning(endpoints);
        var options = Options();
        options.PoolSize = 2;
        options.Tags = ["country:cl"];
        await using var source = new ProxySource(options, client);
        await source.WarmupAsync();

        ProxyEndpoint? result = source.Lease();

        result.ShouldNotBeNull();
        endpoints.Select(e => e.Id).ShouldContain(result!.Id);
        _ = client.Received(1).RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // 4. Report with a negative outcome quarantines the proxy locally so Lease stops returning it —
    // without waiting for the server.
    [Fact]
    public async Task Report_With_Negative_Outcome_Should_Quarantine_Locally_So_Lease_Stops_Returning_It()
    {
        var endpoints = Endpoints(3);
        var client = ClientReturning(endpoints);
        var options = Options();
        options.PoolSize = 3;
        options.Tags = ["country:cl"];
        await using var source = new ProxySource(options, client);
        await source.WarmupAsync();

        ProxyEndpoint first = source.Lease("country:cl")!;
        source.Report(first.Id, ProxyOutcome.Failure, "connect refused");

        for (int i = 0; i < 10; i++)
        {
            source.Lease("country:cl")!.Id.ShouldNotBe(first.Id);
        }
    }

    // Fix round 1, Important 3: Report's local quarantine must also be visible through GetProxies, not
    // only Lease — a Level-0 (.NET Framework 4.8) caller's only surface is GetProxies, and
    // IProxySource.Report is explicitly documented to protect "this process's own next
    // Lease/GetProxies call." Reports one of three and asserts the other two still come back.
    [Fact]
    public async Task GetProxies_Should_Not_Return_A_Locally_Quarantined_Proxy()
    {
        var endpoints = Endpoints(3);
        var client = ClientReturning(endpoints);
        var options = Options();
        options.PoolSize = 3;
        options.Tags = ["country:cl"];
        await using var source = new ProxySource(options, client);
        await source.WarmupAsync();

        source.Report(endpoints[0].Id, ProxyOutcome.Failure);

        IReadOnlyList<ProxyEndpoint> result = source.GetProxies("country:cl");

        result.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Skip(1).Select(e => e.Id)).ShouldBeTrue();
    }

    // Companion: if EVERY proxy is quarantined, GetProxies must hand back the full set rather than an
    // empty one — the same "a questionable proxy beats nothing at all" fallback Next() already applies.
    [Fact]
    public async Task GetProxies_Should_Return_The_Full_Set_When_Every_Proxy_Is_Quarantined()
    {
        var endpoints = Endpoints(2);
        var client = ClientReturning(endpoints);
        var options = Options();
        options.PoolSize = 2;
        options.Tags = ["country:cl"];
        await using var source = new ProxySource(options, client);
        await source.WarmupAsync();

        foreach (ProxyEndpoint endpoint in endpoints)
        {
            source.Report(endpoint.Id, ProxyOutcome.Failure);
        }

        IReadOnlyList<ProxyEndpoint> result = source.GetProxies("country:cl");

        result.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Select(e => e.Id)).ShouldBeTrue();
    }

    // Extra, not one of the 7: a Success report must NOT quarantine — quarantining a proxy that just
    // worked would make no sense, and nothing above proves Report's quarantine is conditional on a
    // negative outcome rather than unconditional.
    [Fact]
    public async Task Report_With_Success_Outcome_Should_Not_Quarantine()
    {
        var endpoints = Endpoints(2);
        var client = ClientReturning(endpoints);
        var options = Options();
        options.PoolSize = 2;
        options.Tags = ["country:cl"];
        await using var source = new ProxySource(options, client);
        await source.WarmupAsync();

        ProxyEndpoint first = source.Lease("country:cl")!;
        source.Report(first.Id, ProxyOutcome.Success);

        var seen = new HashSet<Guid>();
        for (int i = 0; i < 4; i++)
        {
            seen.Add(source.Lease("country:cl")!.Id);
        }
        seen.ShouldContain(first.Id);
    }

    // 5. Report enqueues to the buffer — proven via the buffer's own final flush on disposal, the
    // same technique FeedbackBufferTests uses to pin Enqueue against the real (fake-transport) type.
    [Fact]
    public async Task Report_Should_Enqueue_To_The_Feedback_Buffer()
    {
        var client = ClientReturning(Endpoints(1));
        var options = Options();
        options.Tags = ["country:cl"];
        var source = new ProxySource(options, client);
        await source.WarmupAsync();
        var proxyId = Guid.NewGuid();

        source.Report(proxyId, ProxyOutcome.Failure, "boom");
        await source.DisposeAsync();

        _ = client.Received(1).RequestFeedbackAsync(
            Arg.Is<IReadOnlyList<FeedbackItem>>(events =>
                events.Count == 1 &&
                events[0].ProxyId == proxyId &&
                events[0].Outcome == ProxyOutcome.Failure &&
                events[0].Detail == "boom"),
            Arg.Any<CancellationToken>());
    }

    // 6. Instance throws a clear, actionable error if used before Initialize.
    [Fact]
    public void Instance_Should_Throw_A_Clear_Error_If_Used_Before_Initialize()
    {
        ProxySource.ResetInstanceForTests();
        try
        {
            InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => ProxySource.Instance);
            exception.Message.ShouldContain(nameof(ProxySource.Initialize));
        }
        finally
        {
            ProxySource.ResetInstanceForTests();
        }
    }

    // Extra, not one of the 7: Initialize should actually make Instance usable and stable — nothing
    // above proves the "happy path" the null-check in Instance exists to guard.
    [Fact]
    public async Task Initialize_Then_Instance_Should_Return_The_Same_Configured_Source()
    {
        ProxySource.ResetInstanceForTests();
        var options = Options();
        options.Tags = ["country:cl"];
        ProxySource.Initialize(options);
        IProxySource instance = ProxySource.Instance;
        try
        {
            instance.ShouldBeSameAs(ProxySource.Instance);
        }
        finally
        {
            await ((IAsyncDisposable)instance).DisposeAsync();
            ProxySource.ResetInstanceForTests();
        }
    }

    // 7. Disposal flushes feedback and stops the refresh timer.
    [Fact]
    public async Task DisposeAsync_Should_Flush_Feedback_And_Stop_The_Refresh_Timer()
    {
        var client = ClientReturning(Endpoints(2));
        var options = Options();
        options.Tags = ["country:cl"];
        var source = new ProxySource(options, client);
        await source.WarmupAsync();
        var proxyId = Guid.NewGuid();
        source.Report(proxyId, ProxyOutcome.Failure, "dying scrape");

        Timer? refreshTimer = source.GetRefreshTimerForTests(["country:cl"]);
        refreshTimer.ShouldNotBeNull("WarmupAsync must have started the default pool's refresh timer.");

        await source.DisposeAsync();

        _ = client.Received(1).RequestFeedbackAsync(
            Arg.Is<IReadOnlyList<FeedbackItem>>(events => events.Count == 1 && events[0].ProxyId == proxyId),
            Arg.Any<CancellationToken>());

        // A disposed timer must never fire again. Fix round 1 fold-in: re-arming a disposed timer is
        // platform-divergent — net10 reports failure with a false return (verified empirically),
        // while .NET Framework raises ObjectDisposedException for the identical call. This assertion
        // accepts either outcome as proof of "stopped," since this test suite only runs on net10 but
        // the production code behind it (and this exact call shape) also ships on netstandard2.0.
        bool stoppedForGood;
        try
        {
            stoppedForGood = !refreshTimer!.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            stoppedForGood = true;
        }

        stoppedForGood.ShouldBeTrue("a disposed timer must never fire again — this is what proves DisposeAsync actually stopped it.");
    }

    // Extra: the reactive-refresh mechanism described in the type's remarks — HealthyCount == 0
    // (every proxy quarantined) must trigger a background refresh on the very next Lease/GetProxies
    // call, debounced so a caller spinning on an exhausted pool cannot turn every call into a fresh
    // transport request. Deterministic: the fake transport's Task is already completed, so the
    // dispatched refresh actually runs to completion before Lease returns — no sleep needed.
    [Fact]
    public async Task Lease_Should_Trigger_A_Debounced_Reactive_Refresh_When_The_Pool_Is_Exhausted()
    {
        var initial = Endpoints(2);
        var client = ClientReturning(initial);
        var options = Options();
        options.PoolSize = 2; // so 0 healthy is unambiguously below half, and 2/2 healthy is unambiguously not
        options.Tags = ["country:cl"];
        var clock = new FakeClock();
        await using var source = new ProxySource(options, client, clock.Get);
        await source.WarmupAsync();

        foreach (ProxyEndpoint endpoint in initial)
        {
            source.Report(endpoint.Id, ProxyOutcome.Failure);
        }

        var refreshed = Endpoints(2);
        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProxyEndpoint>>(refreshed));

        // Triggers the reactive refresh (HealthyCount was 0 for the fully-quarantined initial set).
        _ = source.Lease("country:cl");

        var seen = new HashSet<Guid>();
        for (int i = 0; i < 4; i++)
        {
            seen.Add(source.Lease("country:cl")!.Id);
        }
        seen.SetEquals(refreshed.Select(e => e.Id)).ShouldBeTrue("the reactive refresh must have replaced the snapshot with the fresh, non-quarantined set.");

        // Exactly two transport calls so far: the original warmup, and the one reactive refresh above.
        _ = client.Received(2).RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Exhausting the now-fresh pool again immediately must NOT trigger a further refresh yet —
        // the debounce window has not elapsed.
        foreach (ProxyEndpoint endpoint in refreshed)
        {
            source.Report(endpoint.Id, ProxyOutcome.Failure);
        }
        _ = source.Lease("country:cl");
        _ = client.Received(2).RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Once the debounce window elapses, an exhausted pool triggers another refresh.
        clock.Now += TimeSpan.FromSeconds(16);
        _ = source.Lease("country:cl");
        _ = client.Received(3).RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // Fix round 1, Important 2: spec's threshold is 50% of PoolSize, not "every proxy gone." Warms a
    // pool of 10 and quarantines 6 (leaving 4/10 = 40% healthy, still short of the 5/10 the formula
    // needs) to prove the refresh fires well before full exhaustion.
    [Fact]
    public async Task Lease_Should_Trigger_A_Reactive_Refresh_At_Forty_Percent_Healthy_Without_Full_Exhaustion()
    {
        var initial = Endpoints(10);
        var client = ClientReturning(initial);
        var options = Options();
        options.PoolSize = 10;
        options.Tags = ["country:cl"];
        await using var source = new ProxySource(options, client);
        await source.WarmupAsync();

        // Quarantine 6 of 10: HealthyCount == 4, and 4 * 2 (== 8) < PoolSize (== 10) — below the 50%
        // line, even though 4 proxies are still technically usable and none of them are exhausted.
        foreach (ProxyEndpoint endpoint in initial.Take(6))
        {
            source.Report(endpoint.Id, ProxyOutcome.Failure);
        }

        var refreshed = Endpoints(10);
        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProxyEndpoint>>(refreshed));

        _ = source.Lease("country:cl");

        // A second transport call (the reactive refresh) must have happened despite 4 of 10 still
        // being perfectly usable — proving the trigger is the 50%-of-PoolSize line, not "0 left."
        _ = client.Received(2).RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // Fix round 1, Important 1: a tag set discovered purely through GetProxies (never explicitly
    // warmed) must still get a standing refresh timer, not just the one-off reactive refresh that
    // fills it initially — otherwise it only ever recovers by staying at or below 50% healthy long
    // enough for another Lease/GetProxies call to happen to notice.
    [Fact]
    public async Task GetProxies_On_A_Never_Warmed_Tag_Set_Should_Start_Its_Refresh_Timer_Immediately()
    {
        var client = ClientReturning(Endpoints(2));
        var options = Options();
        await using var source = new ProxySource(options, client); // WarmupAsync is never called

        _ = source.GetProxies("country:mx"); // a tag set this source has never seen before

        Timer? timer = source.GetRefreshTimerForTests(["country:mx"]);
        timer.ShouldNotBeNull("a pool discovered purely through GetProxies must start a standing refresh timer, not rely solely on reactive refresh.");
    }

    // Extra: the pure jitter calculation ProxySource.NextRefreshDelay wraps around a real Random —
    // tested directly against a fixed sample, per the task's own testability requirement ("keep the
    // timer-driven work behind a method you can call directly from a test").
    [Theory]
    [InlineData(0.0, -20)] // sample 0 -> multiplier = 1 - jitter
    [InlineData(1.0, 20)]  // sample 1 (exclusive in practice, but the formula is continuous) -> 1 + jitter
    [InlineData(0.5, 0)]   // sample 0.5 -> multiplier = 1, i.e. exactly the base interval
    public void ComputeJitteredDelay_Should_Apply_The_Expected_Percentage_Offset(double sample, int expectedPercentOffset)
    {
        var baseInterval = TimeSpan.FromSeconds(100);

        TimeSpan delay = ProxySource.ComputeJitteredDelay(baseInterval, jitterPercent: 20, randomSample: sample);

        double expectedSeconds = 100 * (1 + (expectedPercentOffset / 100.0));
        delay.TotalSeconds.ShouldBe(expectedSeconds, 0.001);
    }

    [Fact]
    public void ComputeJitteredDelay_With_Zero_Jitter_Should_Always_Return_The_Base_Interval()
    {
        var baseInterval = TimeSpan.FromSeconds(90);

        ProxySource.ComputeJitteredDelay(baseInterval, jitterPercent: 0, randomSample: 0.0).ShouldBe(baseInterval);
        ProxySource.ComputeJitteredDelay(baseInterval, jitterPercent: 0, randomSample: 1.0).ShouldBe(baseInterval);
    }
}
