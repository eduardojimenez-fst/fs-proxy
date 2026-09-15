using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FSH.Proxy.Client.Transport;

namespace FSH.Proxy.Client.Feedback;

/// <summary>
/// Buffers usage outcomes reported through <see cref="Enqueue"/> and flushes them in batches to
/// <see cref="IProxyServiceClient.RequestFeedbackAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Enqueue"/> never blocks and never throws.</b> Feedback is telemetry describing a
/// scrape attempt; it must never be able to throttle or break the scrape it describes. The queue is
/// bounded by <c>ProxyClientOptions.FeedbackQueueCapacity</c> — on overflow it drops the event and
/// counts it in <see cref="DroppedCount"/>, it does not wait for room. This holds even if a
/// caller-supplied <c>sampler</c> delegate itself throws: a throwing sampler is treated as "do not
/// transmit this Success", never as a reason for <see cref="Enqueue"/> to propagate.
/// </para>
/// <para>
/// <b><see cref="ProxyOutcome.Success"/> is not transmitted at default settings.</b> The service's
/// <c>PolicyEvaluationService</c> counts only non-Success events when deciding whether to disable a
/// proxy, so transmitting successes would write rows no decision ever reads.
/// <c>ProxyClientOptions.SuccessSampling</c> defaults to 0 and exists only for a caller who wants the
/// server-side series anyway; every non-Success outcome is always enqueued regardless of this
/// setting.
/// </para>
/// <para>
/// <b>This type is passive</b>, like <c>Pool.ProxyPool</c>: it exposes <see cref="FlushAsync"/> but
/// owns no timer of its own — the timer belongs to <c>ProxySource</c> (a later task), which schedules
/// flushes on <c>ProxyClientOptions.FeedbackFlushInterval</c> and calls in.
/// </para>
/// </remarks>
public sealed class FeedbackBuffer : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Upper bound on how long <see cref="Dispose"/>/<see cref="DisposeAsync"/> waits for the final
    /// flush. A shutdown that hangs on telemetry is its own bug — these scrapers are batch processes
    /// that die quickly, and this must never be the reason one does not.
    /// </summary>
    private static readonly TimeSpan DisposalFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly IProxyServiceClient _client;
    private readonly ProxyClientOptions _options;
    private readonly Func<double> _sampler;
    private readonly ConcurrentQueue<FeedbackItem> _queue = new();

    /// <summary>Current queue length, tracked alongside <see cref="_queue"/> so capacity can be enforced without walking it.</summary>
    private int _count;

    private long _droppedCount;
    private int _disposed;

    public FeedbackBuffer(IProxyServiceClient client, ProxyClientOptions options, Func<double>? sampler = null)
    {
#if NET
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
#else
#pragma warning disable CA1510
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (options is null) throw new ArgumentNullException(nameof(options));
#pragma warning restore CA1510
#endif

        _client = client;
        _options = options;
        _sampler = sampler ?? CreateDefaultSampler();
    }

    /// <summary>
    /// Count of events lost rather than delivered: queue-capacity overflows from <see cref="Enqueue"/>,
    /// plus — once a transport exception hits during a <see cref="FlushAsync"/> call — every event that
    /// call was holding, not merely the batch in flight when the exception fired (see
    /// <see cref="FlushAsync"/>'s remarks). Does NOT include a <see cref="ProxyOutcome.Success"/> event
    /// skipped by <c>ProxyClientOptions.SuccessSampling</c>, and does NOT include one skipped because
    /// the injected sampler itself threw — both are an intentional/defensive "do not transmit", not a
    /// loss.
    /// </summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>Current queue length. Test-only — not part of the public SDK surface.</summary>
    internal int QueuedCount => Interlocked.CompareExchange(ref _count, 0, 0);

    /// <summary>
    /// Records one usage outcome for later delivery. Synchronous, non-blocking, and cannot throw: the
    /// event is either enqueued or dropped-and-counted, and either way the caller's scrape proceeds
    /// unaffected.
    /// </summary>
    public void Enqueue(Guid proxyId, ProxyOutcome outcome, string? detail)
    {
        if (outcome == ProxyOutcome.Success && !ShouldTransmitSuccess())
        {
            return;
        }

        if (Interlocked.Increment(ref _count) > _options.FeedbackQueueCapacity)
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _droppedCount);
            return;
        }

        _queue.Enqueue(new FeedbackItem(proxyId, outcome, detail));
    }

    /// <summary>
    /// Drains the queue in batches of up to <c>ProxyClientOptions.FeedbackBatchSize</c>, submitting
    /// each through <see cref="IProxyServiceClient.RequestFeedbackAsync"/> until the queue is empty. A
    /// call against an empty queue makes no HTTP call at all.
    /// </summary>
    /// <remarks>
    /// Any exception the transport raises is swallowed — feedback must never break the scrape it
    /// describes. On such a failure this call gives up entirely rather than trying the next batch: a
    /// transport that just failed is likely to fail again immediately, and retrying in a tight loop
    /// would turn one call into an unbounded run against an already-down service — a real risk for
    /// <see cref="Dispose"/>/<see cref="DisposeAsync"/>'s bounded shutdown window in particular. So the
    /// failing batch AND everything still queued behind it at that moment are both counted into
    /// <see cref="DroppedCount"/> and removed from the queue: for a periodic caller (<c>ProxySource</c>),
    /// events enqueued after this call returns are unaffected and go out on the next cycle as normal;
    /// for a final flush during disposal, there is no next cycle, so the counter has to reflect the
    /// whole loss right here or it would silently under-report it.
    /// </remarks>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        while (true)
        {
            List<FeedbackItem> batch = DrainBatch();
            if (batch.Count == 0)
            {
                return;
            }

#pragma warning disable CA1031 // A transport failure here (network blip, 5xx, timeout, or the
            // caller's own cancellation racing a disposal timeout) must never propagate out of
            // FlushAsync — feedback describes a scrape, it must not be able to break it. See the
            // method's remarks for why the whole remaining queue is dropped alongside this batch,
            // rather than only this one.
            try
            {
                await _client.RequestFeedbackAsync(batch, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Interlocked.Add(ref _droppedCount, batch.Count);
                DropRemainingQueue(ct);
                return;
            }
#pragma warning restore CA1031
        }
    }

    private List<FeedbackItem> DrainBatch()
    {
        var batch = new List<FeedbackItem>(Math.Min(_options.FeedbackBatchSize, 64));
        for (int i = 0; i < _options.FeedbackBatchSize; i++)
        {
            if (!_queue.TryDequeue(out FeedbackItem? item))
            {
                break;
            }

            Interlocked.Decrement(ref _count);
            batch.Add(item);
        }

        return batch;
    }

    /// <summary>
    /// Drains and counts as dropped everything still in the queue after a transport failure has
    /// already given up on this <see cref="FlushAsync"/> call — see its remarks. Stops early if
    /// <paramref name="ct"/> is already signaled by the time this runs (e.g. a disposal timeout that
    /// fired during the failing transport call itself): whatever is left in that case simply stays
    /// queued, exactly as an ordinary un-drained backlog would.
    /// </summary>
    private void DropRemainingQueue(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _droppedCount);
        }
    }

    private bool ShouldTransmitSuccess()
    {
        double sampling = _options.SuccessSampling;
        if (sampling <= 0)
        {
            return false;
        }

        // >= 1 short-circuits without drawing from the sampler: guarantees "always transmitted" even
        // for a sampler whose contract does not strictly promise a value below 1.0 — and also means a
        // misbehaving sampler is never even called at this setting.
        if (sampling >= 1)
        {
            return true;
        }

#pragma warning disable CA1031 // Enqueue's "never throws" guarantee is only as strong as the sampler a
        // caller injects through the constructor. A sampler that throws is treated the same as "do not
        // transmit this Success" — not as a reason for Enqueue to propagate an exception.
        try
        {
            return _sampler() < sampling;
        }
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }

    // CA5394 ("Random is an insecure RNG") flags any use of System.Random — this sampler decides
    // whether to transmit a Success event for an optional metrics series, not anything
    // security-sensitive, so a cryptographically secure RNG would be pure overhead.
#pragma warning disable CA5394
    private static Func<double> CreateDefaultSampler()
    {
#if NET
        return () => Random.Shared.NextDouble();
#else
        var random = new Random();
        var gate = new object();
        return () =>
        {
            lock (gate)
            {
                return random.NextDouble();
            }
        };
#endif
    }
#pragma warning restore CA5394

    /// <summary>Performs a final, timeout-bounded flush. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using var cts = new CancellationTokenSource(DisposalFlushTimeout);

#pragma warning disable CA1031 // Dispose must never throw regardless of what FlushAsync does under
        // the disposal timeout — see the type's remarks on hanging shutdowns.
        try
        {
            FlushAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Intentionally ignored: FlushAsync already swallows every transport exception itself, so
            // nothing should reach here in practice; this is a last-resort backstop so a shutdown
            // path can never throw out of Dispose no matter what the transport does.
        }
#pragma warning restore CA1031

        GC.SuppressFinalize(this);
    }

    /// <summary>Performs a final, timeout-bounded flush. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using var cts = new CancellationTokenSource(DisposalFlushTimeout);

#pragma warning disable CA1031 // See Dispose(): the final flush must never throw out of disposal.
        try
        {
            await FlushAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Intentionally ignored — see Dispose(): a last-resort backstop, not the primary
            // exception handling, which already lives inside FlushAsync itself.
        }
#pragma warning restore CA1031

        GC.SuppressFinalize(this);
    }
}
