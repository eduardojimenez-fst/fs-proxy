using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    /// <summary>Expiry instant per locally-quarantined proxy id. Pruned lazily as <see cref="Next"/> walks past an expired entry.</summary>
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
    /// How many proxies in the current snapshot are not presently quarantined. Read-only, no I/O —
    /// intended for a caller (e.g. <c>ProxySource</c>'s reactive-refresh check) to decide whether a
    /// pool needs an out-of-band refresh.
    /// </summary>
    public int HealthyCount
    {
        get
        {
            ProxySnapshot snapshot = _snapshot;
            DateTimeOffset now = _clock();
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
    /// Fills the pool for the first time: requests <c>PoolSize</c> proxies from the service and, on
    /// success, both installs the new snapshot and writes it through to the configured
    /// <c>ProxyClientOptions.SnapshotCache</c> (if any). If the service throws or answers with an
    /// empty list, falls back to the cache instead — the scenario this exists for is a scraper
    /// starting cold while the proxy service is unreachable, which without a cache would mean the
    /// process cannot start at all.
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

        if (endpoints.Count > 0)
        {
            _snapshot = new ProxySnapshot(endpoints, _clock());
            _options.SnapshotCache?.Write(_cacheKey, endpoints);
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
    /// Re-requests <c>PoolSize</c> proxies and, on success, replaces the snapshot wholesale by a
    /// single reference assignment — a proxy the service no longer offers simply is not in the new
    /// list and stops being returned. On an empty result (no active proxy for these tags) or any
    /// transport exception, the previous snapshot is left exactly as it was: a transient outage must
    /// not empty the pool out from under every scraper reading it.
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

        if (endpoints.Count == 0)
        {
            // An ordinary, expected state (e.g. the service's 404 for "no active proxy matches
            // these tags") — not an error, and not a reason to empty the pool.
            return;
        }

        _snapshot = new ProxySnapshot(endpoints, _clock());
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

        if (_clock() - snapshot.FetchedAt > _options.StaleCeiling)
        {
            return null;
        }

        DateTimeOffset now = _clock();
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

            // Cooldown lapsed: prune lazily as Next()/HealthyCount walk past it, rather than
            // running a separate sweep. A benign race with a concurrent Quarantine(proxyId) call
            // just means the next read re-evaluates from whatever is present then.
            _quarantine.TryRemove(proxyId, out _);
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
