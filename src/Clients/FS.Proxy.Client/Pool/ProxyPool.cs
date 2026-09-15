using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FSH.Proxy.Client.Transport;

namespace FSH.Proxy.Client.Pool;

/// <summary>
/// Holds one tag set's leased proxies as an immutable, atomically-swapped <see cref="ProxySnapshot"/>
/// and hands them out to callers via <see cref="Next"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The read path (<see cref="Next"/>) never locks and never does I/O.</b> The scrapers consuming
/// this SDK are full of <c>.Result</c>; an async or contended read from that context deadlocks or
/// stalls them, so the whole design revolves around <see cref="Next"/> being a cheap, synchronous,
/// non-blocking reference read. The snapshot is swapped by a single reference assignment, never
/// mutated in place, so a concurrent reader always observes a complete, consistent snapshot.
/// </para>
/// <para>
/// <b>This type is passive.</b> It exposes <see cref="RefreshAsync"/> but owns no timer of its own —
/// the timer belongs to <c>ProxySource</c>, which schedules refreshes on the jittered interval and
/// calls in. That split is what makes every behavior here testable by driving the constructor's
/// injected clock rather than waiting on real time.
/// </para>
/// <para>
/// <b>Degrade, do not fail.</b> An empty result from the service (no active proxy for these tags) or
/// a transport exception (network blip, 5xx, timeout) leaves the previous snapshot serving exactly
/// as it was, up to <c>ProxyClientOptions.StaleCeiling</c> — a 30-second outage must not strand every
/// scraper reading this pool at once. And when every proxy currently held is quarantined,
/// <see cref="Next"/> still returns one rather than <see langword="null"/>: a questionable proxy
/// beats throwing and failing the scrape outright.
/// </para>
/// </remarks>
public sealed class ProxyPool
{
    private readonly IProxyServiceClient _client;
    private readonly ProxyClientOptions _options;
    private readonly IReadOnlyList<string> _tags;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _cacheKey;

    /// <summary>
    /// Expiry instant per locally-quarantined proxy id. An id whose cooldown has lapsed is pruned
    /// lazily as <see cref="Next"/>/<see cref="HealthyCount"/> walk past it; an id the service has
    /// stopped offering entirely is pruned eagerly in <see cref="AcceptFetch"/> against the new
    /// snapshot, since the read path is never going to walk past it otherwise.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _quarantine = new();

    /// <summary>
    /// The current snapshot, swapped by reference on every successful fetch. <c>volatile</c> so a
    /// swap on one thread (inside <see cref="WarmupAsync"/>/<see cref="RefreshAsync"/>) is visible
    /// to a concurrent reader on another without either side taking a lock.
    /// </summary>
    private volatile ProxySnapshot _snapshot;

    /// <summary>Monotonically increasing rotation cursor, advanced with <see cref="Interlocked.Increment(ref long)"/> and taken modulo the snapshot length.</summary>
    private long _cursor;

    public ProxyPool(IProxyServiceClient client, ProxyClientOptions options, IReadOnlyList<string> tags, Func<DateTimeOffset>? clock = null)
    {
#if NET
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tags);
#else
#pragma warning disable CA1510
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (tags is null) throw new ArgumentNullException(nameof(tags));
#pragma warning restore CA1510
#endif

        _client = client;
        _options = options;
        _tags = tags;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _cacheKey = BuildCacheKey(tags);
        _snapshot = ProxySnapshot.Empty(_clock());
    }

    /// <summary>
    /// How many proxies in the current snapshot are not presently quarantined, or <c>0</c> if the
    /// snapshot itself is past <see cref="IsStale"/>. Read-only, no I/O — intended for a caller
    /// (e.g. <c>ProxySource</c>'s reactive-refresh check) to decide whether a pool needs an
    /// out-of-band refresh. The staleness check is deliberate, not an oversight: a stale snapshot is
    /// exactly the one that most needs that out-of-band refresh, and <see cref="Next"/> already
    /// refuses to serve it — a caller gating on <c>HealthyCount &gt; 0</c> would otherwise see a
    /// healthy-looking count for a pool whose <see cref="Next"/> is returning <see langword="null"/>,
    /// and never trigger the recovery it exists to trigger.
    /// </summary>
    public int HealthyCount
    {
        get
        {
            ProxySnapshot snapshot = _snapshot;
            DateTimeOffset now = _clock();
            if (now - snapshot.FetchedAt > _options.StaleCeiling)
            {
                return 0;
            }

            return snapshot.Endpoints.Count(endpoint => !IsQuarantined(endpoint.Id, now));
        }
    }

    /// <summary>
    /// <see langword="true"/> once the current snapshot is older than <c>ProxyClientOptions.StaleCeiling</c>,
    /// per the injected clock. Past this point <see cref="Next"/> refuses to serve it — the whole reason a
    /// stale snapshot is tolerated up to the ceiling at all is to absorb short outages, not to serve
    /// indefinitely-old proxies as if nothing were wrong.
    /// </summary>
    public bool IsStale
    {
        get
        {
            ProxySnapshot snapshot = _snapshot;
            return _clock() - snapshot.FetchedAt > _options.StaleCeiling;
        }
    }

    /// <summary>
    /// The current snapshot's proxies, minus anything currently quarantined — an empty list if the
    /// pool has never been warmed or the snapshot is past <see cref="IsStale"/>. Mirrors
    /// <see cref="Next"/>'s exact degrade rules: a stale snapshot yields nothing, and if
    /// <em>every</em> proxy is quarantined, the quarantine is ignored and the whole set comes back
    /// rather than an empty list — a questionable proxy beats handing the caller nothing at all.
    /// Read-only, no locking, no I/O.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Internal, not public: this type's Produces surface (<see cref="WarmupAsync"/>,
    /// <see cref="RefreshAsync"/>, <see cref="Next"/>, <see cref="Quarantine"/>,
    /// <see cref="HealthyCount"/>, <see cref="IsStale"/>) has no member that hands back the whole
    /// membership at once — only <c>ProxySource</c> (same assembly) needs that, to implement
    /// <c>IProxySource.GetProxies</c>, which — unlike <see cref="Next"/> — gives a caller (e.g. a
    /// scraper managing its own rotation/reputation over the full set) every proxy at once rather
    /// than one at a time. Reconstructing that list by calling <see cref="Next"/> repeatedly would
    /// have worked in principle (it visits every member exactly once per <c>PoolSize</c> calls) but
    /// would have mutated the shared rotation cursor as a side effect of what <c>GetProxies</c>
    /// documents as a pure read — this property avoids that entirely.
    /// </para>
    /// <para>
    /// The quarantine filter matters here specifically because <c>GetProxies</c> is the only surface
    /// a Level-0 (.NET Framework 4.8, no <see cref="Next"/>/<c>Lease</c>) caller ever touches:
    /// <c>ProxySource.Report</c>'s local quarantine has to be visible through this property, or its
    /// "stops offering it on this process's very next call" promise is simply false for that caller.
    /// </para>
    /// <para>
    /// Always returns a wrapper the caller cannot downcast back to a mutable array/list: this is the
    /// first member of this type that ever hands the snapshot's contents out as more than one
    /// <see cref="ProxyEndpoint"/> at a time, so it is also the first place a caller could reach back
    /// in and mutate the live snapshot out from under every other reader — exactly what
    /// <see cref="ProxySnapshot"/>'s own "always defensively copied" constructor invariant exists to
    /// prevent on the way in.
    /// </para>
    /// </remarks>
    internal IReadOnlyList<ProxyEndpoint> Endpoints
    {
        get
        {
            ProxySnapshot snapshot = _snapshot;
            DateTimeOffset now = _clock();
            if (now - snapshot.FetchedAt > _options.StaleCeiling)
            {
                return Array.Empty<ProxyEndpoint>();
            }

            if (snapshot.Endpoints.Count == 0)
            {
                return snapshot.Endpoints;
            }

            List<ProxyEndpoint> healthy = snapshot.Endpoints.Where(endpoint => !IsQuarantined(endpoint.Id, now)).ToList();
            return healthy.Count == 0
                ? new ReadOnlyCollection<ProxyEndpoint>(snapshot.Endpoints.ToList())
                : new ReadOnlyCollection<ProxyEndpoint>(healthy);
        }
    }

    /// <summary>
    /// Fills the pool for the first time: requests <c>PoolSize</c> proxies from the service and, on
    /// success, installs the new snapshot (see <see cref="AcceptFetch"/> — this write-through applies
    /// to every successful fetch, not only a warmup). If the service throws or answers with an empty
    /// list, falls back to the cache instead — the scenario this exists for is a scraper starting
    /// cold while the proxy service is unreachable, which without a cache would mean the process
    /// cannot start at all.
    /// </summary>
    public async Task WarmupAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ProxyEndpoint> endpoints;

#pragma warning disable CA1031 // Warmup must degrade to the cache fallback (or the empty initial
        // snapshot, if there is no cache) rather than fail the caller outright — see the type's
        // remarks. Any exception the transport raises (network failure, non-2xx status, timeout,
        // deserialization failure) is equally "the service didn't answer" from here.
        try
        {
            endpoints = await _client.RequestAsync(_tags, _options.PoolSize, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            endpoints = Array.Empty<ProxyEndpoint>();
        }
#pragma warning restore CA1031

        if (AcceptFetch(endpoints))
        {
            return;
        }

        IReadOnlyList<ProxyEndpoint>? cached = _options.SnapshotCache?.Read(_cacheKey);
        if (cached is { Count: > 0 })
        {
            _snapshot = new ProxySnapshot(cached, _clock());
        }

        // No live result and nothing cached: the snapshot stays whatever it already was (the empty
        // snapshot from construction, on a first warmup). Without a configured cache, a service
        // outage at startup is a hard failure, same as it always was.
    }

    /// <summary>
    /// Re-requests <c>PoolSize</c> proxies and, on success, replaces the snapshot wholesale (see
    /// <see cref="AcceptFetch"/>) — a proxy the service no longer offers simply is not in the new
    /// list and stops being returned. On an empty result (no active proxy for these tags) or any
    /// transport exception, the previous snapshot — and whatever is on disk in
    /// <c>ProxyClientOptions.SnapshotCache</c> — is left exactly as it was: a transient outage must
    /// not empty the pool, or the cache, out from under every scraper reading it.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ProxyEndpoint> endpoints;

#pragma warning disable CA1031 // Catches ALL exceptions from the transport by design: a refresh
        // runs unattended on a background timer (owned by ProxySource, not this type), so nothing
        // is watching to handle a thrown exception — if it escaped here it would tear down whatever
        // is driving the timer instead of just skipping this one refresh. See "Degrade, do not
        // fail" in the type's remarks.
        try
        {
            endpoints = await _client.RequestAsync(_tags, _options.PoolSize, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }
#pragma warning restore CA1031

        // An empty result (e.g. the service's 404 for "no active proxy matches these tags") is an
        // ordinary, expected state, not an error — AcceptFetch is itself a no-op for it, so neither
        // the in-memory snapshot nor the on-disk cache is touched.
        AcceptFetch(endpoints);
    }

    /// <summary>
    /// The single place a fetched proxy list is turned into the pool's new state, shared by
    /// <see cref="WarmupAsync"/> and <see cref="RefreshAsync"/> so both get the same write-through to
    /// <c>ProxyClientOptions.SnapshotCache</c> for free rather than each remembering to call it
    /// separately. A non-empty <paramref name="endpoints"/> both swaps the in-memory snapshot AND
    /// writes it to the cache; an empty one does neither and returns <see langword="false"/> —
    /// crucially, this means a long-running process's on-disk fallback tracks its most recent
    /// successful fetch, not just the one from process startup. Overwriting a good cached snapshot
    /// with an empty result would destroy the fallback the cache exists to provide, so an empty fetch
    /// must never reach <c>IProxySnapshotCache.Write</c>.
    /// </summary>
    /// <returns><see langword="true"/> if <paramref name="endpoints"/> was non-empty and became the new snapshot.</returns>
    private bool AcceptFetch(IReadOnlyList<ProxyEndpoint> endpoints)
    {
        if (endpoints.Count == 0)
        {
            return false;
        }

        _snapshot = new ProxySnapshot(endpoints, _clock());
        PruneRetiredQuarantineEntries(endpoints);
        // IProxySnapshotCache.Write is documented to never throw (see the interface and
        // FileSnapshotCache's remarks) — a cache write is best-effort by contract, so no additional
        // try/catch belongs here.
        _options.SnapshotCache?.Write(_cacheKey, endpoints);
        return true;
    }

    /// <summary>
    /// Drops any quarantine entry whose proxy id is no longer in <paramref name="endpoints"/>. Without
    /// this, an id the service has retired is never walked by <see cref="Next"/>/<see cref="HealthyCount"/>
    /// again — the only two places that otherwise prune a lapsed entry — so it would sit in
    /// <see cref="_quarantine"/> for the remaining life of the process. This runs once per accepted
    /// fetch, off the read path, which is the one point where the id universe actually changes.
    /// </summary>
    private void PruneRetiredQuarantineEntries(IReadOnlyList<ProxyEndpoint> endpoints)
    {
        if (_quarantine.IsEmpty)
        {
            return;
        }

        // HashSet<T>(int capacity) is not available on netstandard2.0 — seed from the sequence
        // instead of pre-sizing.
        var currentIds = new HashSet<Guid>(endpoints.Select(endpoint => endpoint.Id));

        foreach (Guid retiredId in _quarantine.Keys.Where(id => !currentIds.Contains(id)))
        {
            _quarantine.TryRemove(retiredId, out _);
        }
    }

    /// <summary>
    /// Returns the next proxy in rotation, or <see langword="null"/> if the pool has never been
    /// filled or the current snapshot is past <see cref="IsStale"/>. Synchronous, lock-free, and
    /// does no I/O: it reads the current snapshot reference once and rotates over it with an
    /// <see cref="Interlocked"/> counter. When every proxy in the snapshot is currently quarantined,
    /// returns one anyway — a questionable proxy beats failing the scrape.
    /// </summary>
    public ProxyEndpoint? Next()
    {
        ProxySnapshot snapshot = _snapshot;
        IReadOnlyList<ProxyEndpoint> endpoints = snapshot.Endpoints;
        int count = endpoints.Count;
        if (count == 0)
        {
            return null;
        }

        // Read the clock once: the staleness decision and the quarantine decisions below must be
        // made against the same instant, or a clock that advances between two separate reads could
        // make them disagree with each other for no reason a caller could ever observe or explain.
        DateTimeOffset now = _clock();
        if (now - snapshot.FetchedAt > _options.StaleCeiling)
        {
            return null;
        }

        long ticket = Interlocked.Increment(ref _cursor);

        for (int offset = 0; offset < count; offset++)
        {
            int index = (int)((ticket + offset) % count);
            ProxyEndpoint candidate = endpoints[index];
            if (!IsQuarantined(candidate.Id, now))
            {
                return candidate;
            }
        }

        // Every proxy in the snapshot is quarantined. Hand one out anyway rather than returning
        // null: a scraper with a bad-but-usable proxy can still make progress, while one with none
        // at all cannot make any request.
        return endpoints[(int)(ticket % count)];
    }

    /// <summary>
    /// Sets <paramref name="proxyId"/> aside locally for <c>ProxyClientOptions.Quarantine</c>, so
    /// <see cref="Next"/> stops offering it until the cooldown lapses. This is immediate,
    /// process-local self-protection — it does not, by itself, report anything to the service.
    /// </summary>
    public void Quarantine(Guid proxyId)
    {
        _quarantine[proxyId] = _clock() + _options.Quarantine;
    }

    private bool IsQuarantined(Guid proxyId, DateTimeOffset now)
    {
        if (_quarantine.TryGetValue(proxyId, out DateTimeOffset expiresAt))
        {
            if (expiresAt > now)
            {
                return true;
            }

            // Cooldown lapsed: prune lazily as Next()/HealthyCount walk past it, rather than running
            // a separate sweep. Value-comparing removal, not a plain TryRemove(proxyId, ...): a
            // concurrent Quarantine(proxyId) call could have just installed a fresh, still-live
            // expiry for this same id between the TryGetValue above and here, and an unconditional
            // removal would delete THAT entry instead of the lapsed one actually observed — a lost
            // update that hands out a just-re-quarantined proxy one extra time. Removing only if the
            // stored value still equals the one just read closes that window. The failure mode this
            // guards against is benign and self-correcting either way (the next read re-evaluates
            // whatever is present then) — this is about the comment being honest, not about a defect.
            ((ICollection<KeyValuePair<Guid, DateTimeOffset>>)_quarantine)
                .Remove(new KeyValuePair<Guid, DateTimeOffset>(proxyId, expiresAt));
        }

        return false;
    }

    /// <summary>
    /// Derives this pool's <c>IProxySnapshotCache</c> key from its tag set: normalized the same way
    /// the transport normalizes tags, then sorted so tag order never changes the key.
    /// </summary>
    private static string BuildCacheKey(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            return "(no-tags)";
        }

        IOrderedEnumerable<string> normalized = tags.Select(ProxyTags.Normalize).OrderBy(tag => tag, StringComparer.Ordinal);
        return string.Join("|", normalized);
    }
}
