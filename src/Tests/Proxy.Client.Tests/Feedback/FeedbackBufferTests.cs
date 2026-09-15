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
}
