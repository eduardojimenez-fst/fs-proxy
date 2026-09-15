using System.Net.Http;
using FSH.Proxy.Client;
using FSH.Proxy.Client.Feedback;
using FSH.Proxy.Client.Transport;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests.Feedback;

public sealed class FeedbackBufferTests
{
    private static ProxyClientOptions Options() => new()
    {
        BaseAddress = new Uri("https://proxy.test"),
        ApiKey = "key",
        FeedbackBatchSize = 50,
        FeedbackQueueCapacity = 10_000,
        SuccessSampling = 0,
    };

    private static IProxyServiceClient FakeClient() => Substitute.For<IProxyServiceClient>();

    // 1. Enqueue then FlushAsync sends the events once.
    [Fact]
    public async Task Enqueue_Then_FlushAsync_Should_Send_The_Event_Once()
    {
        var client = FakeClient();
        var buffer = new FeedbackBuffer(client, Options());
        var proxyId = Guid.NewGuid();

        buffer.Enqueue(proxyId, ProxyOutcome.Failure, "connect refused");
        await buffer.FlushAsync(CancellationToken.None);

        _ = client.Received(1).RequestFeedbackAsync(
            Arg.Is<IReadOnlyList<FeedbackItem>>(events =>
                events.Count == 1 &&
                events[0].ProxyId == proxyId &&
                events[0].Outcome == ProxyOutcome.Failure &&
                events[0].Detail == "connect refused"),
            Arg.Any<CancellationToken>());

        // A second flush against a now-empty queue must not resend the same event — pins "once",
        // not merely "at least once".
        await buffer.FlushAsync(CancellationToken.None);
        _ = client.Received(1).RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>());
    }

    // 2. Success is not transmitted at default settings (SuccessSampling = 0).
    [Fact]
    public async Task Success_Should_Not_Be_Transmitted_At_Default_Settings()
    {
        var client = FakeClient();
        var options = Options();
        options.SuccessSampling.ShouldBe(0); // guards the premise of this test
        var buffer = new FeedbackBuffer(client, options);

        buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Success, null);
        await buffer.FlushAsync(CancellationToken.None);

        _ = client.DidNotReceive().RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>());
        // A sampled-out Success is a policy decision, not an overflow — must not inflate DroppedCount,
        // which behavior 4 owns exclusively.
        buffer.DroppedCount.ShouldBe(0);
    }

    // Extra, not one of the 8: non-Success outcomes must be unaffected by SuccessSampling — the
    // filter is Success-specific, not a global sampler. Without this, a regression that sampled
    // every outcome (not only Success) would still pass test 2 above.
    [Fact]
    public async Task NonSuccess_Outcomes_Should_Always_Be_Transmitted_Regardless_Of_SuccessSampling()
    {
        var client = FakeClient();
        var options = Options();
        options.SuccessSampling = 0;
        var buffer = new FeedbackBuffer(client, options);

        buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Failure, null);
        buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Banned, null);
        buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Timeout, null);
        await buffer.FlushAsync(CancellationToken.None);

        _ = client.Received(1).RequestFeedbackAsync(
            Arg.Is<IReadOnlyList<FeedbackItem>>(events => events.Count == 3),
            Arg.Any<CancellationToken>());
    }

    // 3. With SuccessSampling = 1.0, successes are transmitted.
    [Fact]
    public async Task With_SuccessSampling_One_Successes_Should_Be_Transmitted()
    {
        var client = FakeClient();
        var options = Options();
        options.SuccessSampling = 1.0;
        var buffer = new FeedbackBuffer(client, options);
        var proxyId = Guid.NewGuid();

        buffer.Enqueue(proxyId, ProxyOutcome.Success, null);
        await buffer.FlushAsync(CancellationToken.None);

        _ = client.Received(1).RequestFeedbackAsync(
            Arg.Is<IReadOnlyList<FeedbackItem>>(events => events.Count == 1 && events[0].ProxyId == proxyId),
            Arg.Any<CancellationToken>());
    }

    // Extra: pins the direction of the sampling comparison (sampler() < threshold), which the pure
    // boundary tests (0 and 1) cannot distinguish from a reversed or off-by-one comparison.
    [Fact]
    public async Task SuccessSampling_Should_Use_The_Injected_Sampler_Against_The_Threshold()
    {
        var client = FakeClient();
        var options = Options();
        options.SuccessSampling = 0.5;

        var below = new FeedbackBuffer(client, options, () => 0.3);
        below.Enqueue(Guid.NewGuid(), ProxyOutcome.Success, null);
        await below.FlushAsync(CancellationToken.None);
        _ = client.Received(1).RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>());

        client.ClearReceivedCalls();

        var above = new FeedbackBuffer(client, options, () => 0.7);
        above.Enqueue(Guid.NewGuid(), ProxyOutcome.Success, null);
        await above.FlushAsync(CancellationToken.None);
        _ = client.DidNotReceive().RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>());
    }

    // I4: the server's validator requires a non-empty ProxyId and rejects the WHOLE batch — every
    // event in it, not only the offending one — if any single event violates that. Screening
    // Guid.Empty out at Enqueue time keeps one caller bug from poisoning the rest of the backlog.
    [Fact]
    public async Task Enqueue_Should_Drop_And_Count_An_Event_With_An_Empty_ProxyId()
    {
        var client = FakeClient();
        var buffer = new FeedbackBuffer(client, Options());

        buffer.Enqueue(Guid.Empty, ProxyOutcome.Failure, "boom");
        await buffer.FlushAsync(CancellationToken.None);

        buffer.DroppedCount.ShouldBe(1);
        buffer.QueuedCount.ShouldBe(0);
        _ = client.DidNotReceive().RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>());
    }

    // Companion: a valid event enqueued alongside an empty-ProxyId one must still ship — the bad
    // event is dropped, not the whole call.
    [Fact]
    public async Task Enqueue_Should_Still_Accept_A_Valid_Event_Enqueued_Alongside_An_Empty_ProxyId_One()
    {
        var client = FakeClient();
        var buffer = new FeedbackBuffer(client, Options());
        var validId = Guid.NewGuid();

        buffer.Enqueue(Guid.Empty, ProxyOutcome.Failure, null);
        buffer.Enqueue(validId, ProxyOutcome.Failure, null);
        await buffer.FlushAsync(CancellationToken.None);

        buffer.DroppedCount.ShouldBe(1);
        _ = client.Received(1).RequestFeedbackAsync(
            Arg.Is<IReadOnlyList<FeedbackItem>>(events => events.Count == 1 && events[0].ProxyId == validId),
            Arg.Any<CancellationToken>());
    }

    // I4: the server's validator caps Detail at 2048 characters and rejects the whole batch if any
    // one event exceeds it. Truncating (not dropping) keeps the event's outcome — the important
    // half of the signal — while staying inside what the server will accept.
    [Fact]
    public async Task Enqueue_Should_Truncate_Detail_To_The_Servers_2048_Character_Cap()
    {
        var client = FakeClient();
        var buffer = new FeedbackBuffer(client, Options());
        var proxyId = Guid.NewGuid();
        string oversized = new string('x', 5000);

        buffer.Enqueue(proxyId, ProxyOutcome.Failure, oversized);
        await buffer.FlushAsync(CancellationToken.None);

        _ = client.Received(1).RequestFeedbackAsync(
            Arg.Is<IReadOnlyList<FeedbackItem>>(events => events.Count == 1 && events[0].ProxyId == proxyId && events[0].Detail!.Length == 2048),
            Arg.Any<CancellationToken>());
        buffer.DroppedCount.ShouldBe(0, "truncation must not count as a drop — the event still ships, just shortened.");
    }

    // Companion: a detail already within the cap must reach the transport byte-for-byte, unchanged.
    [Fact]
    public async Task Enqueue_Should_Not_Alter_A_Detail_Already_Within_The_Cap()
    {
        var client = FakeClient();
        var buffer = new FeedbackBuffer(client, Options());
        var proxyId = Guid.NewGuid();

        buffer.Enqueue(proxyId, ProxyOutcome.Failure, "connect refused");
        await buffer.FlushAsync(CancellationToken.None);

        _ = client.Received(1).RequestFeedbackAsync(
            Arg.Is<IReadOnlyList<FeedbackItem>>(events => events.Count == 1 && events[0].Detail == "connect refused"),
            Arg.Any<CancellationToken>());
    }

    // 4. Enqueueing past FeedbackQueueCapacity drops rather than blocking, and DroppedCount rises.
    [Fact]
    public async Task Enqueue_Past_Capacity_Should_Drop_And_Raise_DroppedCount()
    {
        var client = FakeClient();
        var options = Options();
        options.FeedbackQueueCapacity = 3;
        var buffer = new FeedbackBuffer(client, options);

        for (int i = 0; i < 5; i++)
        {
            buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Failure, null);
        }

        // Non-blocking is a structural property of Enqueue (no lock, no await, no I/O) rather than
        // something a timing-based unit test could pin without flakiness; verified here by the fact
        // that five synchronous calls returned at all, and by code inspection at review time.
        buffer.DroppedCount.ShouldBe(2);

        await buffer.FlushAsync(CancellationToken.None);
        _ = client.Received(1).RequestFeedbackAsync(
            Arg.Is<IReadOnlyList<FeedbackItem>>(events => events.Count == 3),
            Arg.Any<CancellationToken>());

        // Important 1 (fix round 1): capacity freed by a successful flush must actually be usable
        // again — not permanently wedged at "full". Deleting the Interlocked.Decrement(ref _count) in
        // DrainBatch would leave the buffer stuck dropping everything from here on; this call would
        // then land in DroppedCount instead of being accepted.
        var newProxyId = Guid.NewGuid();
        buffer.Enqueue(newProxyId, ProxyOutcome.Failure, null);
        buffer.DroppedCount.ShouldBe(2, "capacity freed by the flush above must be usable again, not permanently exhausted.");

        await buffer.FlushAsync(CancellationToken.None);
        _ = client.Received(1).RequestFeedbackAsync(
            Arg.Is<IReadOnlyList<FeedbackItem>>(events => events.Count == 1 && events[0].ProxyId == newProxyId),
            Arg.Any<CancellationToken>());
    }

    // 5. FlushAsync splits a queue larger than FeedbackBatchSize into multiple calls.
    [Fact]
    public async Task FlushAsync_Should_Split_A_Queue_Larger_Than_BatchSize_Into_Multiple_Calls()
    {
        var client = FakeClient();
        var options = Options();
        options.FeedbackBatchSize = 2;
        options.FeedbackQueueCapacity = 100;
        var buffer = new FeedbackBuffer(client, options);

        for (int i = 0; i < 5; i++)
        {
            buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Failure, null);
        }

        var batchSizes = new List<int>();
        client.RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(callInfo => batchSizes.Add(callInfo.Arg<IReadOnlyList<FeedbackItem>>().Count));

        await buffer.FlushAsync(CancellationToken.None);

        batchSizes.ShouldBe([2, 2, 1]);
    }

    // 6. FlushAsync on an empty queue makes no HTTP call.
    [Fact]
    public async Task FlushAsync_On_An_Empty_Queue_Should_Make_No_Http_Call()
    {
        var client = FakeClient();
        var buffer = new FeedbackBuffer(client, Options());

        await buffer.FlushAsync(CancellationToken.None);

        _ = client.DidNotReceive().RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>());
    }

    // 7. A transport exception during flush does not propagate to the caller.
    [Fact]
    public async Task Transport_Exception_During_Flush_Should_Not_Propagate()
    {
        var client = FakeClient();
        client.RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("boom")));
        var buffer = new FeedbackBuffer(client, Options());
        buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Failure, null);

        await Should.NotThrowAsync(() => buffer.FlushAsync(CancellationToken.None));

        // Resolved ambiguity: a batch lost to a transport failure is counted as dropped too — it was
        // already dequeued (and so is gone) at the moment the exception is caught, same as an
        // overflow drop from the caller's point of view: telemetry that never reached the service.
        buffer.DroppedCount.ShouldBe(1);
    }

    // 8. Disposal performs a final flush (synchronous Dispose).
    [Fact]
    public void Dispose_Should_Perform_A_Final_Flush()
    {
        var client = FakeClient();
        var proxyId = Guid.NewGuid();
        var buffer = new FeedbackBuffer(client, Options());
        buffer.Enqueue(proxyId, ProxyOutcome.Failure, "dying scrape");

        buffer.Dispose();

        _ = client.Received(1).RequestFeedbackAsync(
            Arg.Is<IReadOnlyList<FeedbackItem>>(events => events.Count == 1 && events[0].ProxyId == proxyId),
            Arg.Any<CancellationToken>());
    }

    // 8. Disposal performs a final flush (asynchronous DisposeAsync).
    [Fact]
    public async Task DisposeAsync_Should_Perform_A_Final_Flush()
    {
        var client = FakeClient();
        var proxyId = Guid.NewGuid();
        var buffer = new FeedbackBuffer(client, Options());
        buffer.Enqueue(proxyId, ProxyOutcome.Failure, "dying scrape");

        await buffer.DisposeAsync();

        _ = client.Received(1).RequestFeedbackAsync(
            Arg.Is<IReadOnlyList<FeedbackItem>>(events => events.Count == 1 && events[0].ProxyId == proxyId),
            Arg.Any<CancellationToken>());
    }

    // Extra: disposal's final flush must not hang forever against a transport that never returns —
    // "a shutdown that hangs on telemetry is its own bug" per the brief. A transport call that never
    // completes must still let Dispose return within a bounded time.
    [Fact]
    public async Task Dispose_Should_Not_Hang_When_The_Transport_Never_Responds()
    {
        var client = FakeClient();
        client.RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ct = callInfo.Arg<CancellationToken>();
                // Never completes on its own — only cancellation (Dispose's bounded timeout) ends it.
                return Task.Delay(Timeout.Infinite, ct);
            });
        var buffer = new FeedbackBuffer(client, Options());
        buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Failure, null);

        Task disposeTask = Task.Run(() => buffer.Dispose());
        Task firstCompleted = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(15)));

        firstCompleted.ShouldBe(disposeTask, "Dispose must return within a bounded timeout, not hang on a stuck transport call.");
    }

    // I3: a transport failure during an ORDINARY (periodic) FlushAsync call must drop only the batch
    // that actually hit the failure — everything still queued behind it was NEVER SENT, so
    // re-queueing it cannot duplicate anything, and discarding it too would throw away real,
    // unsent signal (often exactly the Banned/Timeout events a policy decision most needs) over one
    // transient failure. Batch size 2 over 5 events means the first DrainBatch() pulls 2, the
    // transport throws on that call, and 3 more are still sitting in the queue at that moment — only
    // those first 2 land in DroppedCount; the remaining 3 must stay queued for the next cycle.
    [Fact]
    public async Task Transport_Exception_During_Periodic_Flush_Should_Only_Drop_The_Failed_Batch_And_Retain_The_Remainder()
    {
        var client = FakeClient();
        client.RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("boom")));
        var options = Options();
        options.FeedbackBatchSize = 2;
        var buffer = new FeedbackBuffer(client, options);
        for (int i = 0; i < 5; i++)
        {
            buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Failure, null);
        }

        await Should.NotThrowAsync(() => buffer.FlushAsync(CancellationToken.None));

        buffer.DroppedCount.ShouldBe(2);
        buffer.QueuedCount.ShouldBe(3, "events never sent to the failing transport must survive for the next flush cycle, not be discarded alongside the batch that actually failed.");
        // Exactly one transport attempt: once it fails, this FlushAsync call must not keep hammering
        // an already-failing service with the remaining batches instead of giving up.
        _ = client.Received(1).RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>());

        // The companion half: those 3 retained events are still there for the NEXT cycle to pick up
        // once the transport recovers.
        client.ClearReceivedCalls();
        client.RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await buffer.FlushAsync(CancellationToken.None);
        _ = client.Received(1).RequestFeedbackAsync(Arg.Is<IReadOnlyList<FeedbackItem>>(events => events.Count == 2), Arg.Any<CancellationToken>());
        _ = client.Received(1).RequestFeedbackAsync(Arg.Is<IReadOnlyList<FeedbackItem>>(events => events.Count == 1), Arg.Any<CancellationToken>());
        buffer.QueuedCount.ShouldBe(0);
    }

    // I3's other half: there IS no next cycle for the terminal disposal flush, so — unlike the
    // periodic path above — a transport failure there drops and counts the ENTIRE remaining queue,
    // not only the batch that hit the failure. This is the old (pre-I3) unconditional behavior,
    // preserved deliberately for exactly this one call site.
    [Fact]
    public void Dispose_Should_Count_The_Entire_Remaining_Queue_As_Dropped_When_The_Final_Flush_Fails()
    {
        var client = FakeClient();
        client.RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("boom")));
        var options = Options();
        options.FeedbackBatchSize = 2;
        var buffer = new FeedbackBuffer(client, options);
        for (int i = 0; i < 5; i++)
        {
            buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Failure, null);
        }

        Should.NotThrow(() => buffer.Dispose());

        buffer.DroppedCount.ShouldBe(5);
        buffer.QueuedCount.ShouldBe(0);
        _ = client.Received(1).RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>());
    }

    // Fix round 1, Important 3: both final-flush tests above (Dispose_Should_Perform_A_Final_Flush,
    // DisposeAsync_Should_Perform_A_Final_Flush) rely on NSubstitute's default for an unconfigured
    // Task-returning member, which is an ALREADY-COMPLETED Task — so a fire-and-forget Dispose (one
    // that starts FlushAsync but does not wait for it) would still show Received(1) before Dispose
    // returns, and those tests would not notice. This test configures the transport to complete only
    // after a real delay and gates the assertion on a signal set from INSIDE that delayed completion,
    // so it can only pass if Dispose/DisposeAsync genuinely waited for the call to finish.
    [Fact]
    public void Dispose_Should_Not_Return_Before_The_Final_Flush_Actually_Completes()
    {
        var client = FakeClient();
        var flushCompleted = new ManualResetEventSlim(initialState: false);
        client.RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.Delay(TimeSpan.FromMilliseconds(300)).ContinueWith(
                _ => flushCompleted.Set(), TaskScheduler.Default));
        var buffer = new FeedbackBuffer(client, Options());
        buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Failure, null);

        buffer.Dispose();

        flushCompleted.IsSet.ShouldBeTrue("Dispose must not return before the final flush actually completes.");
    }

    // Fix round 1, Important 3 (async half) — same reasoning as the sync test above.
    [Fact]
    public async Task DisposeAsync_Should_Not_Complete_Before_The_Final_Flush_Actually_Completes()
    {
        var client = FakeClient();
        var flushCompleted = new ManualResetEventSlim(initialState: false);
        client.RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.Delay(TimeSpan.FromMilliseconds(300)).ContinueWith(
                _ => flushCompleted.Set(), TaskScheduler.Default));
        var buffer = new FeedbackBuffer(client, Options());
        buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Failure, null);

        await buffer.DisposeAsync();

        flushCompleted.IsSet.ShouldBeTrue("DisposeAsync must not complete before the final flush actually completes.");
    }

    // Fix round 1, Important 4: the brief frames Enqueue as called from many scraper threads while a
    // background flush drains concurrently. This hammers Enqueue from several threads against a small
    // bounded capacity with NO concurrent flush (so nothing drains mid-race), which makes the queue's
    // FINAL length a direct, deterministic witness of whether the reserve-then-commit capacity check
    // ever let the queue exceed its cap: since nothing ever dequeues in this test, QueuedCount can only
    // grow, so its value at the end is not just "a snapshot" but the high-water mark for the whole run.
    [Fact]
    public async Task Enqueue_Should_Never_Exceed_Capacity_Under_Concurrent_Callers()
    {
        var client = FakeClient();
        var options = Options();
        options.FeedbackQueueCapacity = 500;
        var buffer = new FeedbackBuffer(client, options);

        const int threadCount = 8;
        const int perThread = 300; // 2,400 attempts against a capacity of 500
        var start = new ManualResetEventSlim(initialState: false);
        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < perThread; i++)
                {
                    buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Failure, null);
                }
            });
        }

        start.Set();
        await Task.WhenAll(tasks);

        int totalAttempts = threadCount * perThread;
        buffer.QueuedCount.ShouldBe(options.FeedbackQueueCapacity, "the queue must never exceed FeedbackQueueCapacity, even under concurrent Enqueue calls.");
        buffer.DroppedCount.ShouldBe(totalAttempts - options.FeedbackQueueCapacity, "every attempt must be accounted for exactly once, as either queued or dropped.");
    }

    // Fix round 1, Important 4 (companion): the scenario above proves capacity enforcement is race-free
    // in isolation; this one exercises Enqueue and FlushAsync running CONCURRENTLY — the actual shape
    // ProxySource (Task 8) drives in production, with scrapers reporting outcomes while a timer flushes
    // in the background. Exact end-to-end accounting (every attempt is either sent or dropped, with no
    // double-count and nothing silently lost) is the property a race in either the capacity check or
    // the drain/dequeue path would break.
    [Fact]
    public async Task Enqueue_And_FlushAsync_Should_Account_For_Every_Event_Exactly_Under_Concurrent_Use()
    {
        var client = FakeClient();
        int totalSent = 0;
        client.RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(callInfo => Interlocked.Add(ref totalSent, callInfo.Arg<IReadOnlyList<FeedbackItem>>().Count));

        var options = Options();
        options.FeedbackQueueCapacity = 500;
        options.FeedbackBatchSize = 25;
        var buffer = new FeedbackBuffer(client, options);

        const int threadCount = 8;
        const int perThread = 300;
        var start = new ManualResetEventSlim(initialState: false);
        var enqueueTasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            enqueueTasks[t] = Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < perThread; i++)
                {
                    buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Failure, null);
                }
            });
        }

        using var stopFlushing = new CancellationTokenSource();
        Task flushLoop = Task.Run(async () =>
        {
            start.Wait();
            while (!stopFlushing.IsCancellationRequested)
            {
                await buffer.FlushAsync(CancellationToken.None);
                await Task.Yield();
            }
        });

        start.Set();
        await Task.WhenAll(enqueueTasks);
        await stopFlushing.CancelAsync();
        await flushLoop;
        // Drain whatever the flush loop's last iteration missed.
        await buffer.FlushAsync(CancellationToken.None);

        int totalAttempts = threadCount * perThread;
        (totalSent + buffer.DroppedCount).ShouldBe(totalAttempts, "every enqueued event must end up either sent or dropped exactly once — never both, never neither.");
        buffer.QueuedCount.ShouldBe(0);
    }

    // Fix round 1, fold-in: Enqueue's "never throws" guarantee must hold even when the sampler a
    // caller injected through the constructor misbehaves — the constructor makes providing a custom
    // sampler first-class, so it must be treated as untrusted input, not assumed well-behaved.
    [Fact]
    public async Task Enqueue_Should_Not_Throw_And_Should_Not_Transmit_When_The_Injected_Sampler_Throws()
    {
        var client = FakeClient();
        var options = Options();
        options.SuccessSampling = 0.5;
        var buffer = new FeedbackBuffer(client, options, () => throw new InvalidOperationException("boom"));

        Should.NotThrow(() => buffer.Enqueue(Guid.NewGuid(), ProxyOutcome.Success, null));

        await Should.NotThrowAsync(() => buffer.FlushAsync(CancellationToken.None));
        _ = client.DidNotReceive().RequestFeedbackAsync(Arg.Any<IReadOnlyList<FeedbackItem>>(), Arg.Any<CancellationToken>());
    }
}
