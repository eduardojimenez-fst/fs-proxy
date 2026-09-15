using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FSH.Proxy.Client.Feedback;
using FSH.Proxy.Client.Pool;
using FSH.Proxy.Client.Transport;

namespace FSH.Proxy.Client;

/// <summary>
/// The facade every adoption level of this SDK uses: one <see cref="ProxyPool"/> per normalized tag
/// set, a shared <see cref="FeedbackBuffer"/>, and the timers that keep both moving without a caller
/// ever having to drive them.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="GetProxies"/> and <see cref="Lease"/> are synchronous and perform no I/O.</b> This is
/// the single most important property of this type. The scrapers consuming this SDK — the legacy .NET
/// Framework 4.8 ones especially — are full of <c>.Result</c>/<c>.Wait()</c>; if either method awaited
/// the transport, or blocked on a Task that needed a captured context those scrapers are already
/// blocking, the result is a deadlock, not a slow request. Both methods only ever read an
/// already-warmed <see cref="ProxyPool"/>'s in-memory snapshot (see <see cref="ProxyPool.Next"/> and
/// <see cref="ProxyPool.Endpoints"/>, both themselves lock-free and I/O-free) and, at most, dispatch a
/// background refresh without waiting for it — see "Reactive refresh" below.
/// </para>
/// <para>
/// <b>This type owns the timers that <see cref="ProxyPool"/> and <see cref="FeedbackBuffer"/>
/// deliberately do not.</b> Both of those types expose a `*Async` method to drive them but schedule
/// nothing themselves, specifically so their own tests never have to wait on real time. Here, a
/// <see cref="System.Threading.Timer"/> per pool re-arms itself after every tick with a freshly
/// jittered delay (<see cref="ComputeJitteredDelay"/>) — <c>RefreshInterval * (1 ± RefreshJitterPercent
/// / 100)</c>, drawn per refresh so that many scrapers started together do not stay in lockstep — and
/// one further shared <see cref="System.Threading.Timer"/> flushes the feedback buffer on
/// <c>ProxyClientOptions.FeedbackFlushInterval</c>.
/// </para>
/// <para>
/// <b>Reactive refresh.</b> Every <see cref="GetProxies"/>/<see cref="Lease"/> call checks the pool it
/// just read and, when <see cref="ProxyPool.HealthyCount"/> has fallen below half of
/// <c>ProxyClientOptions.PoolSize</c> — the spec's own threshold, not merely "every proxy is gone" —
/// dispatches (never awaits) a <see cref="ProxyPool.RefreshAsync"/> call, debounced per pool so a
/// scraper hammering a degraded pool in a tight loop cannot turn this into a refresh storm. A stale
/// snapshot and a snapshot with more than half its proxies quarantined both read as "below half
/// healthy" the same way, and a brand-new, never-warmed pool (<c>HealthyCount == 0</c>) does too —
/// see <see cref="GetOrCreatePoolEntry"/>'s own remarks for why a non-default tag set does not need a
/// separate warmup path to get filled.
/// </para>
/// <para>
/// <b><see cref="Report"/> does two things, on purpose.</b> A non-<see cref="ProxyOutcome.Success"/>
/// outcome quarantines the reported proxy id on every pool this source currently knows about
/// (harmless on a pool that never held it — see <see cref="ProxyPool.Quarantine"/>'s own
/// contract), so this process stops offering it on its very next call, without waiting for the
/// server. The outcome is also always enqueued to the shared <see cref="FeedbackBuffer"/>, which is
/// what lets the service's policy engine decide for the whole fleet once it is flushed. Dropping
/// either half looks like a simplification and is not: without the quarantine, this run keeps
/// re-offering a proxy it already watched fail until a round trip to the server catches up; without
/// the enqueue, nothing outside this one process — including this same process on its next run — ever
/// learns anything happened.
/// </para>
/// </remarks>
public sealed class ProxySource : IProxySource, IAsyncDisposable
{
    /// <summary>
    /// Floor under a reactive refresh's debounce, per pool. Long enough that a scraper looping tightly
    /// over an exhausted pool cannot turn every <see cref="GetProxies"/>/<see cref="Lease"/> call into
    /// a fresh transport request; short enough that a real recovery (the service coming back, or a
    /// quarantine cooling down) is picked up promptly rather than waiting for the next scheduled tick.
    /// </summary>
    private static readonly TimeSpan ReactiveRefreshDebounce = TimeSpan.FromSeconds(15);

    private static IProxySource? s_instance;

    private readonly ProxyClientOptions _options;
    private readonly IProxyServiceClient _client;
    private readonly HttpClient? _ownedHttpClient;
    private readonly FeedbackBuffer _feedbackBuffer;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, PoolEntry> _pools = new(StringComparer.Ordinal);
    private readonly object _feedbackTimerGate = new();
    private Timer? _feedbackTimer;
    private int _disposed;

#if !NET
    // CA5394 below covers why plain Random is fine here; netstandard2.0 has no Random.Shared, so a
    // single lock-guarded instance stands in for it — same pattern FeedbackBuffer's default sampler
    // already uses for the same reason. Static (not per-instance): NextRandomSample touches no
    // instance state on either target, so it is itself static — see its own remarks.
    private static readonly Random s_random = new();
    private static readonly object s_randomGate = new();
#endif

    /// <summary>
    /// Builds a source that owns its own <see cref="HttpClient"/> — the constructor a DI-less .NET
    /// Framework 4.8 scraper (or anything else with no container) calls directly. The owned
    /// <see cref="HttpClient"/> is disposed alongside this instance.
    /// </summary>
    public ProxySource(ProxyClientOptions options)
        : this(CreateOwnedTransport(options))
    {
    }

    /// <summary>
    /// Injects a caller-supplied <see cref="IProxyServiceClient"/> (and, optionally, a fake clock)
    /// instead of building an owned <see cref="HttpClient"/>. Originally a test-only seam; also now the
    /// production call site the net10 DI extensions use (<c>ServiceCollectionExtensions.AddFsProxyClient</c>)
    /// to hand this type an <see cref="IProxyServiceClient"/> built from an <c>IHttpClientFactory</c>-managed
    /// <see cref="HttpClient"/> rather than one this instance would otherwise own and have to dispose
    /// itself.
    /// </summary>
    internal ProxySource(ProxyClientOptions options, IProxyServiceClient client, Func<DateTimeOffset>? clock = null)
        : this(options, client, ownedHttpClient: null, clock)
    {
    }

    private ProxySource((ProxyClientOptions Options, IProxyServiceClient Client, HttpClient OwnedHttpClient) owned)
        : this(owned.Options, owned.Client, owned.OwnedHttpClient, clock: null)
    {
    }

    private ProxySource(ProxyClientOptions options, IProxyServiceClient client, HttpClient? ownedHttpClient, Func<DateTimeOffset>? clock)
    {
#if NET
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
#else
#pragma warning disable CA1510
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (client is null) throw new ArgumentNullException(nameof(client));
#pragma warning restore CA1510
#endif

        options.Validate();

        _options = options;
        _client = client;
        _ownedHttpClient = ownedHttpClient;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _feedbackBuffer = new FeedbackBuffer(client, options);

        // Started here, not only from WarmupAsync: Report()/Enqueue() must be able to reach the
        // service even for a caller that never calls WarmupAsync at all (a Level-0 scraper's
        // simplest possible integration is Initialize + GetProxies + Report, with no explicit
        // warmup step) — feedback would otherwise sit in the buffer until this source is disposed.
        // This is also the point where `this` escapes to another thread (the timer's callback closes
        // over it via OnFeedbackFlushTick): it must be the LAST statement in this constructor, since
        // every field the callback can touch (_disposed, _feedbackBuffer) has to already be assigned
        // by the time an instant first tick could possibly run. Any field added above this line is
        // safe for the callback to read; one added below it would not be.
        EnsureFeedbackTimerStarted();
    }

    /// <summary>
    /// The process-wide instance for callers with no DI container. Throws with an actionable message
    /// if read before <see cref="Initialize"/> — a DI-less scraper hitting a bare
    /// <see cref="NullReferenceException"/> here would have no way to guess why.
    /// </summary>
    public static IProxySource Instance =>
        s_instance ?? throw new InvalidOperationException(
            "ProxySource.Instance was read before ProxySource.Initialize(ProxyClientOptions) was called. " +
            "Call ProxySource.Initialize(options) once at process startup — typically right after " +
            "building your ProxyClientOptions — before using ProxySource.Instance anywhere.");

    /// <summary>
    /// Builds the process-wide <see cref="Instance"/> from <paramref name="options"/>. A plain static
    /// assignment with a null check on read (see <see cref="Instance"/>) — deliberately not a
    /// lazily-initialized, double-checked-locked singleton: this is a once-at-startup call, not a
    /// contended hot path, so that machinery would only add risk for no benefit.
    /// </summary>
    public static void Initialize(ProxyClientOptions options) => s_instance = new ProxySource(options);

    /// <summary>Test-only: clears <see cref="Instance"/> back to its pre-<see cref="Initialize"/> state.</summary>
    internal static void ResetInstanceForTests() => s_instance = null;

    /// <summary>
    /// Test-only: exposes a tag set's underlying refresh <see cref="Timer"/>, so a test can prove
    /// disposal actually stopped it — a disposed <see cref="Timer"/>'s <see cref="Timer.Change(int,int)"/>
    /// returns <see langword="false"/> instead of re-arming it, a deterministic, non-timing-based way
    /// to observe "this will never fire again" without waiting on real time.
    /// </summary>
    internal Timer? GetRefreshTimerForTests(IReadOnlyList<string> tags) =>
        _pools.TryGetValue(BuildKey(tags), out PoolEntry? entry) ? entry.RefreshTimer : null;

    /// <inheritdoc />
    public IReadOnlyList<ProxyEndpoint> GetProxies(params string[] tags)
    {
        PoolEntry entry = GetOrCreatePoolEntry(EffectiveTags(tags));
        MaybeTriggerReactiveRefresh(entry);
        return entry.Pool.Endpoints;
    }

    /// <inheritdoc />
    public ProxyEndpoint? Lease(params string[] tags)
    {
        PoolEntry entry = GetOrCreatePoolEntry(EffectiveTags(tags));
        MaybeTriggerReactiveRefresh(entry);
        return entry.Pool.Next();
    }

    /// <summary>
    /// Falls back to <c>ProxyClientOptions.Tags</c> when the caller supplied none — mirroring what
    /// <see cref="ProxyClientOptions.Tags"/> itself is documented to mean ("tags this client leases
    /// against when none are passed explicitly"). Without this, <c>GetProxies()</c>/<c>Lease()</c>
    /// with no arguments would create and lease from a separate, untagged pool instead of the
    /// configured default — silently handing back proxies for the wrong country/entity type.
    /// </summary>
    private string[] EffectiveTags(string[]? tags) => tags is null || tags.Length == 0 ? _options.Tags : tags;

    /// <inheritdoc />
    public void Report(Guid proxyId, ProxyOutcome outcome, string? detail = null)
    {
        // See the type's remarks ("Report does two things, on purpose") for why both of these run,
        // unconditionally, rather than one standing in for the other.
        if (outcome != ProxyOutcome.Success)
        {
            foreach (PoolEntry entry in _pools.Values)
            {
                entry.Pool.Quarantine(proxyId);
            }
        }

        _feedbackBuffer.Enqueue(proxyId, outcome, detail);
    }

    /// <inheritdoc />
    public async Task WarmupAsync(CancellationToken ct = default)
    {
        EnsureFeedbackTimerStarted();
        PoolEntry entry = GetOrCreatePoolEntry(_options.Tags);
        await entry.Pool.WarmupAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Looks up (or, on first sight of this tag set, constructs) the <see cref="ProxyPool"/> for the
    /// normalized, sorted tag set — one pool per distinct tag set, per the type's remarks. Construction
    /// alone performs no I/O, so this stays safe to call from <see cref="GetProxies"/>/<see cref="Lease"/>.
    /// </summary>
    /// <remarks>
    /// A brand-new pool's refresh timer is started right here, at creation — not only from
    /// <see cref="WarmupAsync"/> — so that a tag set discovered purely through
    /// <see cref="GetProxies"/>/<see cref="Lease"/> (never explicitly warmed) still gets periodic
    /// refreshes going forward, not just the one-off reactive refresh <see cref="MaybeTriggerReactiveRefresh"/>
    /// dispatches for it. Without this, spec's 60-120s-with-jitter refresh requirement would only ever
    /// apply to the default tag set, and every other pool would depend entirely on staying at or above
    /// half of <c>PoolSize</c> — recovering only when it happened to dip back below that and a caller
    /// happened to call in again.
    /// </remarks>
    private PoolEntry GetOrCreatePoolEntry(IReadOnlyList<string> tags)
    {
        string key = BuildKey(tags);
        if (_pools.TryGetValue(key, out PoolEntry? existing))
        {
            return existing;
        }

        // The same clock this source uses for its own debounce bookkeeping is threaded through to
        // every pool it creates, so a test-injected fake clock controls staleness/quarantine timing
        // consistently across the whole facade, not just the reactive-refresh debounce.
        var candidate = new PoolEntry(new ProxyPool(_client, _options, NormalizeTagList(tags), _clock));
        PoolEntry stored = _pools.GetOrAdd(key, candidate);
        if (ReferenceEquals(stored, candidate))
        {
            // This call won the race to register the tag set: it alone starts the timer, so a race
            // between two callers discovering the same brand-new tag set at once cannot start two.
            StartRefreshTimer(stored);
        }

        return stored;
    }

    /// <summary>
    /// Dispatches (never awaits) a <see cref="ProxyPool.RefreshAsync"/> call for <paramref name="entry"/>
    /// when its <see cref="ProxyPool.HealthyCount"/> has fallen below half of <c>ProxyClientOptions.PoolSize</c>
    /// (the spec's own threshold — not merely "every proxy is gone"), debounced by
    /// <see cref="ReactiveRefreshDebounce"/> so a caller spinning on a degraded pool cannot turn this
    /// into a refresh storm. See the type's remarks ("Reactive refresh").
    /// </summary>
    private void MaybeTriggerReactiveRefresh(PoolEntry entry)
    {
        // HealthyCount * 2 >= PoolSize, rearranged to avoid a division — naturally covers HealthyCount
        // == 0 as the extreme case, but does not wait for it: 26 of 50 quarantined already qualifies.
        if (entry.Pool.HealthyCount * 2 >= _options.PoolSize)
        {
            return;
        }

        long nowTicks = _clock().UtcTicks;
        long lastTicks = Interlocked.Read(ref entry.LastReactiveRefreshTicksUtc);
        if (lastTicks != 0 && nowTicks - lastTicks < ReactiveRefreshDebounce.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref entry.LastReactiveRefreshTicksUtc, nowTicks, lastTicks) != lastTicks)
        {
            // Another thread just won the race to trigger this cycle's reactive refresh; no need
            // for this one to also fire.
            return;
        }

        _ = entry.Pool.RefreshAsync();
    }

    /// <summary>
    /// Starts <paramref name="entry"/>'s own jittered refresh timer, unless it already has one. Called
    /// once, from the single call site in <see cref="GetOrCreatePoolEntry"/> that wins the race to
    /// register a brand-new tag set — the guard below is a defensive backstop, not the primary
    /// idempotency mechanism.
    /// </summary>
    private void StartRefreshTimer(PoolEntry entry)
    {
        if (entry.RefreshTimer is not null)
        {
            return;
        }

        lock (entry)
        {
            if (entry.RefreshTimer is not null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            entry.RefreshTimer = new Timer(_ => OnRefreshTick(entry), null, NextRefreshDelay(), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnRefreshTick(PoolEntry entry)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _ = RunRefreshTickAsync(entry);
    }

    private async Task RunRefreshTickAsync(PoolEntry entry)
    {
        // ProxyPool.RefreshAsync never throws (it degrades to "keep the previous snapshot" on any
        // transport failure — see its own remarks), so there is nothing to catch here: the only job
        // left is re-arming the timer for the next jittered tick, and only if this source is not
        // mid-disposal.
        await entry.Pool.RefreshAsync().ConfigureAwait(false);

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // The _disposed check above is check-then-act, not a lock: DisposeAsync can dispose this exact
        // Timer between that read and the Change call below. On net10's System.Threading.Timer,
        // Change on a disposed timer returns false; on .NET Framework's Timer (this SDK's other
        // target, netstandard2.0), the same call throws ObjectDisposedException instead — a genuine,
        // verified platform divergence, not a hypothetical one. Caught, not avoided: there is no
        // "check disposed" that closes the race, so the only real fix is tolerating the outcome either
        // platform can produce.
        try
        {
            entry.RefreshTimer?.Change(NextRefreshDelay(), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Lost the race to DisposeAsync between the check above and this call — nothing to do,
            // the source is shutting down.
        }
    }

    private void EnsureFeedbackTimerStarted()
    {
        if (_feedbackTimer is not null)
        {
            return;
        }

        lock (_feedbackTimerGate)
        {
            if (_feedbackTimer is not null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _feedbackTimer = new Timer(OnFeedbackFlushTick, null, _options.FeedbackFlushInterval, _options.FeedbackFlushInterval);
        }
    }

    private void OnFeedbackFlushTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // The buffer's flush is documented to never throw, so there is nothing to catch or re-arm
        // here; this timer is already periodic (fixed period, no jitter — only the per-pool refresh
        // needs jitter, to avoid many scrapers' transport requests synchronizing, which a feedback
        // flush against the caller's own service is not exposed to in the same way). Discarded on
        // purpose: this callback must not block the timer thread waiting for the flush to finish.
        _ = _feedbackBuffer.FlushAsync();
    }

    /// <summary>
    /// The next refresh delay: <c>RefreshInterval * (1 ± RefreshJitterPercent / 100)</c>, drawn fresh
    /// for every call. See <see cref="ComputeJitteredDelay"/> for the pure calculation this wraps.
    /// </summary>
    private TimeSpan NextRefreshDelay() => ComputeJitteredDelay(_options.RefreshInterval, _options.RefreshJitterPercent, NextRandomSample());

    /// <summary>
    /// The pure half of the jitter calculation, split out from <see cref="NextRefreshDelay"/> so it is
    /// directly unit-testable against a fixed <paramref name="randomSample"/> without any timer or
    /// real randomness involved.
    /// </summary>
    /// <param name="baseInterval"><c>ProxyClientOptions.RefreshInterval</c>.</param>
    /// <param name="jitterPercent"><c>ProxyClientOptions.RefreshJitterPercent</c>, 0-100.</param>
    /// <param name="randomSample">A value in [0, 1), one fresh draw per call.</param>
    internal static TimeSpan ComputeJitteredDelay(TimeSpan baseInterval, int jitterPercent, double randomSample)
    {
        // randomSample in [0, 1) maps to a multiplier in [1 - jitter, 1 + jitter). Drawn fresh per
        // refresh (not a fixed per-instance offset) so that many scrapers started at the same instant
        // spread out across the whole window instead of drifting back into lockstep after one cycle.
        double normalized = (randomSample * 2) - 1; // [-1, 1)
        double multiplier = 1 + (normalized * jitterPercent / 100.0);
        double delayMs = baseInterval.TotalMilliseconds * multiplier;

        // A Timer's due time must never be negative; ProxyClientOptions.Validate already keeps
        // RefreshJitterPercent within [0, 100], so this floor is a defensive backstop, not a path any
        // valid configuration can reach.
        return TimeSpan.FromMilliseconds(Math.Max(delayMs, 1));
    }

    // CA5394 ("Random is an insecure RNG"): this decides when a background refresh timer fires next,
    // purely to spread load across many scraper instances — not a security-sensitive decision, so a
    // cryptographically secure RNG would be pure overhead. Mirrors FeedbackBuffer's own default
    // sampler, which makes the identical argument for the identical pattern.
#pragma warning disable CA5394
    private static double NextRandomSample()
    {
#if NET
        return Random.Shared.NextDouble();
#else
        lock (s_randomGate)
        {
            return s_random.NextDouble();
        }
#endif
    }
#pragma warning restore CA5394

    /// <summary>
    /// Derives a pool's dictionary key from its tag set: each tag normalized (mirrors the server's own
    /// normalization — see <see cref="ProxyTags.Normalize"/>), then sorted ordinally, so that neither
    /// casing nor argument order can ever address two different pools for what is really one tag set.
    /// Mirrors <c>ProxyPool</c>'s own (private) cache-key derivation exactly, for the same reason.
    /// </summary>
    private static string BuildKey(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            return "(no-tags)";
        }

        IOrderedEnumerable<string> normalized = tags.Select(ProxyTags.Normalize).OrderBy(tag => tag, StringComparer.Ordinal);
        return string.Join("|", normalized);
    }

    /// <summary>Normalizes (without sorting) the tags a new <see cref="ProxyPool"/> is constructed against.</summary>
    private static List<string> NormalizeTagList(IReadOnlyList<string> tags) => tags.Select(ProxyTags.Normalize).ToList();

    private static (ProxyClientOptions Options, IProxyServiceClient Client, HttpClient OwnedHttpClient) CreateOwnedTransport(ProxyClientOptions options)
    {
        // Validate BEFORE allocating the HttpClient this instance would otherwise own: the private
        // constructor this feeds into also calls options.Validate(), but by then the HttpClient would
        // already exist with nothing left ever able to dispose it — the constructor never finishes,
        // so it never reaches _ownedHttpClient, and the exception unwinds past this factory with the
        // handle already leaked. A null `options` still throws from here with the right parameter
        // name, matching ProxyServiceClient's own guard.
#if NET
        ArgumentNullException.ThrowIfNull(options);
#else
#pragma warning disable CA1510
        if (options is null) throw new ArgumentNullException(nameof(options));
#pragma warning restore CA1510
#endif
        options.Validate();

        var httpClient = new HttpClient();
        var client = new ProxyServiceClient(httpClient, options);
        return (options, client, httpClient);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // System.Threading.Timer implements IAsyncDisposable on net10 (not on netstandard2.0, where
        // only the synchronous Dispose() exists) — use whichever this target actually has, since
        // we're already inside an async method.
#if NET
        if (_feedbackTimer is not null)
        {
            await _feedbackTimer.DisposeAsync().ConfigureAwait(false);
        }

        foreach (Timer timer in _pools.Values.Select(entry => entry.RefreshTimer).Where(t => t is not null)!)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }
#else
        _feedbackTimer?.Dispose();
        foreach (PoolEntry entry in _pools.Values)
        {
            entry.RefreshTimer?.Dispose();
        }
#endif

        // Performs the final, timeout-bounded flush — see FeedbackBuffer's own remarks.
        await _feedbackBuffer.DisposeAsync().ConfigureAwait(false);

        _ownedHttpClient?.Dispose();
    }

    /// <summary>One tag set's pool, plus the mutable state <see cref="ProxySource"/> tracks alongside it.</summary>
    private sealed class PoolEntry
    {
        public PoolEntry(ProxyPool pool)
        {
            Pool = pool;
        }

        public ProxyPool Pool { get; }

        /// <summary>This pool's own refresh timer, created once by <see cref="StartRefreshTimer"/>.</summary>
        public Timer? RefreshTimer { get; set; }

        /// <summary><see cref="DateTimeOffset.UtcTicks"/> of this pool's last reactive refresh, or <c>0</c> if none yet.</summary>
        public long LastReactiveRefreshTicksUtc;
    }
}
