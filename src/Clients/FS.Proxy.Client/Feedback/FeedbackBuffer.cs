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
/// <b>This type is passive</b>, like <c>Pool.ProxyPool</c>: it exposes <see cref="FlushAsync(CancellationToken)"/> but
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

    /// <summary>
    /// The server's validator caps <c>Detail</c> at 2048 characters and rejects the WHOLE batch if
    /// any one event exceeds it — see <see cref="Enqueue"/>'s own remarks.
    /// </summary>
    private const int MaxDetailLength = 2048;

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
    /// Count of events lost rather than delivered: queue-capacity overflows and screened-out events
    /// (an empty <c>proxyId</c>) from <see cref="Enqueue"/>, plus whatever a <see cref="FlushAsync(CancellationToken)"/>
    /// transport failure drops — the batch that actually hit the failure always, and, ONLY for the
    /// terminal disposal flush (<see cref="Dispose"/>/<see cref="DisposeAsync"/>), everything still
    /// queued behind it too (see <see cref="FlushAsync(CancellationToken)"/>'s remarks for why the periodic path keeps
    /// the remainder instead). Does NOT include a <see cref="ProxyOutcome.Success"/> event skipped by
    /// <c>ProxyClientOptions.SuccessSampling</c>, and does NOT include one skipped because the
    /// injected sampler itself threw — both are an intentional/defensive "do not transmit", not a
    /// loss. Does NOT include <c>detail</c> truncation — that event still ships, just shortened.
    /// </summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>Current queue length. Test-only — not part of the public SDK surface.</summary>
    internal int QueuedCount => Interlocked.CompareExchange(ref _count, 0, 0);

    /// <summary>
    /// Records one usage outcome for later delivery. Synchronous, non-blocking, and cannot throw: the
    /// event is either enqueued or dropped-and-counted, and either way the caller's scrape proceeds
    /// unaffected.
    /// </summary>
    /// <remarks>
    /// Two additional guards protect the REST of the batch this event will ship in, not just this
    /// one event: the server's validator rejects the whole submitted batch — every event in it, not
    /// only the offending one — if any single event's <c>Detail</c> exceeds 2048 characters or its
    /// <c>ProxyId</c> is empty. Left unguarded, one oversized exception message or one
    /// <c>Report(Guid.Empty, …)</c> call turns every flush that batch is part of into a 400, which
    /// <see cref="FlushAsync(CancellationToken)"/> then has to treat as a transport failure — discarding real signal
    /// about OTHER, unrelated proxies over a single caller mistake, and doing so repeatedly if the
    /// offending exception recurs. <c>detail</c> is truncated rather than rejected (it is still
    /// useful information, just capped at what the server will accept); an empty <c>proxyId</c> is
    /// dropped outright and counted in <see cref="DroppedCount"/>, since there is nothing valid to
    /// attach it to.
    /// </remarks>
    public void Enqueue(Guid proxyId, ProxyOutcome outcome, string? detail)
    {
        if (outcome == ProxyOutcome.Success && !ShouldTransmitSuccess())
        {
            return;
        }

        if (proxyId == Guid.Empty)
        {
            Interlocked.Increment(ref _droppedCount);
            return;
        }

        string? truncatedDetail = detail is { Length: > MaxDetailLength } ? detail.Substring(0, MaxDetailLength) : detail;

        if (Interlocked.Increment(ref _count) > _options.FeedbackQueueCapacity)
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _droppedCount);
            return;
        }

        _queue.Enqueue(new FeedbackItem(proxyId, outcome, truncatedDetail));
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
    /// would turn one call into an unbounded run against an already-down service. The batch that
    /// actually hit the failure is counted into <see cref="DroppedCount"/> and removed from the
    /// queue — it may or may not have landed server-side before the exception fired, so re-sending it
    /// risks a duplicate, and the design spec's §4 explicitly rules out blind retry here.
    /// <para>
    /// <b>Everything still queued BEHIND that batch was never sent at all</b>, so re-queueing it
    /// cannot duplicate anything — dropping it too would throw away real, never-transmitted signal
    /// (often exactly the <c>Banned</c>/<c>Timeout</c> events a policy decision most needs) over one
    /// transient failure. This ordinary call (used by <c>ProxySource</c>'s periodic timer and its
    /// size-triggered flush) therefore leaves the remainder queued for the next cycle to pick up —
    /// only the failed batch is dropped. The one exception is the terminal flush
    /// <see cref="Dispose"/>/<see cref="DisposeAsync"/> perform on shutdown: there is no next cycle to
    /// leave anything queued FOR, so that path drops and counts the entire remaining queue too,
    /// exactly as this method used to do unconditionally before this distinction existed.
    /// </para>
    /// </remarks>
    public Task FlushAsync(CancellationToken ct = default) => FlushCoreAsync(isFinalFlush: false, ct);

    /// <summary>
    /// The actual implementation behind the public <see cref="FlushAsync(CancellationToken)"/> and
    /// the terminal disposal flush — see that method's remarks for the one behavioral difference
    /// <paramref name="isFinalFlush"/> controls. A differently-named method rather than a second
    /// <c>FlushAsync</c> overload: an overload taking <c>(CancellationToken, bool)</c> would put
    /// <see cref="CancellationToken"/> in a non-last position (CA1068), and re-ordering it there
    /// would make the two overloads ambiguous at several existing call sites within this type.
    /// </summary>
    private async Task FlushCoreAsync(bool isFinalFlush, CancellationToken ct)
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
            // FlushAsync — feedback describes a scrape, it must not be able to break it. See this
            // method's public overload's remarks for exactly what gets dropped and what does not.
            try
            {
                await _client.RequestFeedbackAsync(batch, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Interlocked.Add(ref _droppedCount, batch.Count);

                if (isFinalFlush)
                {
                    // No next cycle: this is the last chance to account for what remains, so (and
                    // only so) the whole backlog is dropped and counted here too.
                    DropRemainingQueue(ct);
                }

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
    /// already given up on this <see cref="FlushAsync(CancellationToken)"/> call — see its remarks. Stops early if
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
            FlushCoreAsync(isFinalFlush: true, cts.Token).GetAwaiter().GetResult();
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
            await FlushCoreAsync(isFinalFlush: true, cts.Token).ConfigureAwait(false);
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
