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
        options.Tags = ["country:cl"];
        await using var source = new ProxySource(options, client);

        await source.WarmupAsync();
        IReadOnlyList<ProxyEndpoint> result = source.GetProxies("country:cl");

        result.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Select(e => e.Id)).ShouldBeTrue();
    }

    // 2. GetProxies performs no I/O: after warmup, a transport that throws on any call must not stop
    // it from still returning the already-warmed contents. This is the deadlock-safety property the
    // .NET Framework 4.8 scrapers (full of .Result) depend on.
    [Fact]
    public async Task GetProxies_Should_Perform_No_IO_After_Warmup()
    {
        var endpoints = Endpoints(2);
        var client = ClientReturning(endpoints);
        var options = Options();
        options.Tags = ["country:cl"];
        await using var source = new ProxySource(options, client);
        await source.WarmupAsync();

        // Every proxy in the just-warmed pool is healthy, so this call must not even consider a
        // reactive refresh — the transport throwing here is the whole point of the test.
        client.RequestAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ProxyEndpoint>>(new InvalidOperationException("GetProxies must not call the transport.")));
        client.RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("GetProxies must not call the transport.")));

        IReadOnlyList<ProxyEndpoint> result = source.GetProxies("country:cl");

        result.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Select(e => e.Id)).ShouldBeTrue();
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
        options.Tags = ["country:cl", "entitytype:tender"];
        await using var source = new ProxySource(options, client);
        await source.WarmupAsync();

        IReadOnlyList<ProxyEndpoint> a = source.GetProxies("Country:CL", "entityType:Tender");
        IReadOnlyList<ProxyEndpoint> b = source.GetProxies("entitytype:tender", "country:cl");

        a.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Select(e => e.Id)).ShouldBeTrue();
        b.Select(e => e.Id).ToHashSet().SetEquals(endpoints.Select(e => e.Id)).ShouldBeTrue();
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

    // Extra, not one of the 7: a Success report must NOT quarantine — quarantining a proxy that just
    // worked would make no sense, and nothing above proves Report's quarantine is conditional on a
    // negative outcome rather than unconditional.
    [Fact]
    public async Task Report_With_Success_Outcome_Should_Not_Quarantine()
    {
        var endpoints = Endpoints(2);
        var client = ClientReturning(endpoints);
        var options = Options();
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

        // A disposed System.Threading.Timer's Change returns false rather than re-arming (verified
        // empirically against this runtime's System.Threading.Timer — it does not throw here, unlike
        // some other BCL disposables) — the deterministic, non-timing-based way to prove the timer was
        // actually stopped rather than merely "not going to fire again soon by coincidence."
        bool rearmed = refreshTimer!.Change(Timeout.Infinite, Timeout.Infinite);
        rearmed.ShouldBeFalse("a disposed timer must refuse to be re-armed — this is what proves DisposeAsync actually stopped it.");
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
