using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FSH.Proxy.Client.Http;

/// <summary>
/// Adoption level 2 — the convenience for code written from here on. Leases a proxy from
/// <see cref="IProxySource"/> for every outgoing request, sends it through that proxy, and reports
/// the outcome back through <see cref="ProxyOutcomeClassifier"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this cannot be "pick a proxy, then call <c>base.SendAsync</c>."</b>
/// <c>HttpClientHandler.Proxy</c> (and the <c>SocketsHttpHandler</c> it wraps on modern .NET) is set
/// once per handler instance, not per request. A <see cref="DelegatingHandler"/> only ever forwards to
/// whatever <see cref="DelegatingHandler.InnerHandler"/> it was given — it cannot reach into that
/// handler and change its <c>Proxy</c> for one request without racing every other request
/// concurrently in flight through the same (pooled, shared) primary handler. Three ways out of that
/// were considered:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Chosen: this handler owns a small, bounded cache of <see cref="HttpMessageInvoker"/>, one per
/// leased proxy endpoint</b> (keyed by <see cref="ProxyEndpoint.Id"/>, capped at <see cref="MaxCachedInvokers"/>
/// with least-recently-used eviction — see that constant's own remarks for why a hard cap is required
/// and what naive bound does NOT work), each wrapping its own low-level handler built once with that
/// endpoint's <see cref="ProxyEndpoint.ToWebProxy"/> (see <see cref="CreateDefaultPerProxyHandler"/>).
/// <see cref="SendAsync"/> leases a proxy, looks up (or builds) that proxy's invoker, and sends through
/// it directly — it deliberately does NOT call <c>base.SendAsync</c> when a proxy was leased, because
/// doing so would still funnel every request through whatever single primary handler the
/// <c>HttpClient</c> was configured with. <b>The trade-off this buys:</b> whenever a proxy is actually
/// leased, this handler <i>is</i> the terminal handler for that request — a primary handler configured
/// on the owning <c>HttpClient</c> (e.g. via <c>ConfigurePrimaryHttpMessageHandler</c>), any
/// <see cref="DelegatingHandler"/> registered to run <i>after</i> this one in the pipeline, AND
/// <c>IHttpClientFactory</c>'s own automatically-added logging handlers
/// (<c>LoggingHttpMessageHandler</c>/<c>LoggingScopeHttpMessageHandler</c> from <c>AddHttpClient</c>)
/// are all silently skipped for that request. Request/response logging, correlation-id propagation,
/// and any Polly resilience policy wired onto the client's builder simply do not run while a proxy is
/// in play. <see cref="DelegatingHandler.InnerHandler"/> is used only for the no-proxy-available
/// fallback (behaviour 7) — see <see cref="SendDirectAsync"/>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Rejected: require <c>SocketsHttpHandler</c> with a custom, per-request <see cref="IWebProxy"/>.
/// </b> <c>SocketsHttpHandler.Proxy</c> accepts an <see cref="IWebProxy"/> whose <c>GetProxy(Uri)</c> is
/// consulted per connection, so a custom implementation reading from (say) an <see cref="AsyncLocal{T}"/>
/// set just before the call could, in principle, vary the proxy per logical request. Rejected because
/// it pushes real complexity onto every consumer (write and thread a custom <c>IWebProxy</c>, guarantee
/// the primary handler is a bare <c>SocketsHttpHandler</c> and not something else, get the
/// <c>AsyncLocal</c> flow right across the await boundaries between this handler and connection
/// establishment) for a benefit — no separate per-proxy handler cache to bound at all — that does not
/// justify the cost: the cache this design needs instead is now explicitly bounded (see
/// <see cref="MaxCachedInvokers"/>), so the "unbounded growth" concern that would otherwise favor the
/// rejected option does not actually apply here either.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Rejected for now — recorded as a follow-up, not a permanent decision.</b> A reviewer identified a
/// third option: keep this type a true, pass-through <see cref="DelegatingHandler"/> that stashes the
/// leased <see cref="ProxyEndpoint"/> on the request (e.g. <c>HttpRequestMessage.Options</c>) and calls
/// <c>base.SendAsync</c> unconditionally, doing the actual per-proxy routing inside a <i>primary</i>
/// handler installed via <c>ConfigurePrimaryHttpMessageHandler</c> that reads the stashed endpoint back
/// off the request. That preserves the entire <c>IHttpClientFactory</c> pipeline — the logging handlers,
/// correlation-id propagation, and Polly policies this chosen design skips (see above) would all keep
/// running for every request, proxied or not. It is very likely the better long-term design. It was not
/// adopted in this round: this is adoption level 2, explicitly the convenience path for newly-written
/// code rather than the migration path the legacy fleet depends on; the trade-off above is disclosed
/// here in this type's own documentation; and restructuring now was judged more expensive than the
/// value of doing so immediately, given the trade-off is disclosed rather than silent. Tracked as a
/// follow-up.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>Reporting, not control flow.</b> Exactly one outcome is reported per call to
/// <see cref="IProxySource.Report"/>, and only for an attempt that actually went through the leased
/// proxy: a response is always returned and a transport exception is always rethrown unchanged (see
/// behaviour 5) — this handler's only side effect on the happy or unhappy path is that one <c>Report</c>
/// call. <see cref="ProxyOutcomeClassifier.FromResponse"/> also means a destination's own 4xx/5xx (503
/// included) reports <see cref="ProxyOutcome.Success"/>: the proxy did its job, the portal's own
/// problem is not the proxy's fault. The constructor's <c>classifyResponse</c> parameter is the hook
/// for a caller that recognizes a soft block — a 200 response whose body is actually a captcha page,
/// which no generic status-code rule can ever detect and which is the single most valuable signal
/// these scrapers have. <b>That hook runs deliberately outside the try/catch that attributes a failure
/// to the proxy</b> — a bug in a caller's own <c>classifyResponse</c> (say, a
/// <see cref="NullReferenceException"/> walking the response body) must never be misreported as a
/// proxy fault against a proxy that just delivered a perfectly good response. See
/// <see cref="SendAsync"/>'s own remarks for exactly how that isolation works.
/// </para>
/// </remarks>
public sealed class FsProxyRotationHandler : DelegatingHandler
{
    /// <summary>
    /// Upper bound on distinct per-proxy invokers cached at once, with least-recently-used eviction
    /// once exceeded (see <see cref="EvictLeastRecentlyUsedIfOverCapacity"/>).
    /// </summary>
    /// <remarks>
    /// <c>ProxyClientOptions.PoolSize</c> is NOT this bound, and an earlier draft of this type's
    /// documentation wrongly claimed it was. <c>PoolSize</c> caps how many proxies one
    /// <c>ProxyPool</c> holds <i>at once</i>; it says nothing about how many <i>distinct</i>
    /// <see cref="ProxyEndpoint.Id"/>s a long-lived handler could ever see. The pool refreshes on a
    /// jittered ~90s cycle and quarantined proxies get replaced by others from the tag-matched
    /// inventory, so over the life of a long-running process this cache would otherwise converge on
    /// the size of that whole inventory, not <c>PoolSize</c> — and this handler has no access to the
    /// live <c>PoolSize</c> value at all (it is constructed from just an <see cref="IProxySource"/>
    /// and a tag set, not a <c>ProxyClientOptions</c>). Left unbounded, a singleton-captured typed
    /// client (exactly what <c>AddFsProxyRotation</c> produces) or the long-lived, non-DI
    /// <see cref="HttpClient"/> this SDK invites elsewhere would accumulate one low-level handler —
    /// and the connection pool and socket state it owns — per distinct proxy ever leased, for the
    /// whole process lifetime. 100 (twice <c>ProxyClientOptions</c>'s own hard ceiling of 50 — see its
    /// <c>Validate</c>) is generous enough not to evict a healthy, actively-rotating pool's own
    /// members, and small enough that this cache cannot grow without limit.
    /// </remarks>
    internal const int MaxCachedInvokers = 100;

#if NET
    /// <summary>
    /// How long a pooled connection inside one of this handler's <see cref="SocketsHttpHandler"/>s
    /// (per-proxy or the no-proxy direct fallback) may live before a fresh DNS lookup and a new
    /// connection replace it. net10 only — netstandard2.0's <see cref="HttpClientHandler"/> has no
    /// equivalent knob. This matters specifically because this handler's cache is deliberately
    /// long-lived (see <see cref="MaxCachedInvokers"/>): a connection that never expires never
    /// re-resolves DNS either, which is exactly the kind of silent staleness a long-running scraper
    /// process must not accumulate.
    /// </summary>
    private static readonly TimeSpan PerHandlerConnectionLifetime = TimeSpan.FromMinutes(5);
#endif

    private readonly IProxySource _source;
    private readonly string[] _tags;
    private readonly Func<HttpResponseMessage, ProxyOutcome?>? _classifyResponse;
    private readonly Func<ProxyEndpoint, HttpMessageHandler> _perProxyHandlerFactory;
    private readonly Func<HttpMessageHandler> _directHandlerFactory;
    private readonly ConcurrentDictionary<Guid, CacheEntry> _invokers = new();

    /// <summary>
    /// Lazily built the first time <see cref="SendAsync"/> needs to send directly (no proxy leased)
    /// AND <see cref="DelegatingHandler.InnerHandler"/> was never assigned — see
    /// <see cref="SendDirectAsync"/> and behaviour 7's own remarks there.
    /// </summary>
    private readonly Lazy<HttpMessageInvoker> _directInvoker;

    private long _sequence;

    /// <summary>
    /// Builds a handler that leases proxies tagged <paramref name="tags"/> from <paramref name="source"/>.
    /// </summary>
    /// <param name="source">Where proxies are leased from and outcomes are reported to.</param>
    /// <param name="tags">The tag set passed to every <see cref="IProxySource.Lease"/> call this handler makes.</param>
    /// <param name="classifyResponse">
    /// Optional. Recognizes a soft block a status code cannot — see the type's remarks. Returning
    /// <see langword="null"/> falls through to the ordinary status-code classification.
    /// </param>
    public FsProxyRotationHandler(IProxySource source, string[] tags, Func<HttpResponseMessage, ProxyOutcome?>? classifyResponse = null)
        : this(source, tags, classifyResponse, perProxyHandlerFactory: null, directHandlerFactory: null)
    {
    }

    /// <summary>
    /// Test seam: substitutes the factories that turn a leased <see cref="ProxyEndpoint"/> (or, for
    /// <paramref name="directHandlerFactory"/>, the no-proxy-available case) into the low-level
    /// <see cref="HttpMessageHandler"/> that actually sends. Production code always goes through the
    /// public constructor, which defaults both to <see cref="CreateDefaultPerProxyHandler"/> and
    /// <see cref="CreateDefaultDirectHandler"/> respectively.
    /// </summary>
    internal FsProxyRotationHandler(
        IProxySource source,
        string[] tags,
        Func<HttpResponseMessage, ProxyOutcome?>? classifyResponse,
        Func<ProxyEndpoint, HttpMessageHandler>? perProxyHandlerFactory,
        Func<HttpMessageHandler>? directHandlerFactory = null)
    {
#if NET
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tags);
#else
#pragma warning disable CA1510
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (tags is null) throw new ArgumentNullException(nameof(tags));
#pragma warning restore CA1510
#endif

        _source = source;

        // Defensive copy: ProxyEndpoint is meticulous about never letting a caller-owned array keep
        // reaching in after construction, and this type must be too — without this, a caller mutating
        // the array it passed in would silently change every later Lease() call this handler makes.
        _tags = (string[])tags.Clone();

        _classifyResponse = classifyResponse;
        _perProxyHandlerFactory = perProxyHandlerFactory ?? CreateDefaultPerProxyHandler;
        _directHandlerFactory = directHandlerFactory ?? CreateDefaultDirectHandler;
        _directInvoker = new Lazy<HttpMessageInvoker>(() => new HttpMessageInvoker(_directHandlerFactory(), disposeHandler: true));
    }

    /// <summary>
    /// Builds the real, production low-level handler for one proxy endpoint, with
    /// <see cref="ProxyEndpoint.ToWebProxy"/> assigned. On net10, a <c>SocketsHttpHandler</c> with a
    /// pooled-connection lifetime set (see the net10-only <c>PerHandlerConnectionLifetime</c> constant's
    /// remarks for why a long-lived cache needs it); on netstandard2.0, where <c>SocketsHttpHandler</c>
    /// does not exist at all, a plain <see cref="HttpClientHandler"/> — the one primary handler type
    /// available on both of this package's targets. See <see cref="ProxyEndpoint.ToWebProxy"/>'s own
    /// remarks for the SOCKS5 caveat that follows from that netstandard2.0 fallback.
    /// </summary>
    internal static HttpMessageHandler CreateDefaultPerProxyHandler(ProxyEndpoint endpoint)
    {
#if NET
        return new SocketsHttpHandler
        {
            Proxy = endpoint.ToWebProxy(),
            UseProxy = true,
            PooledConnectionLifetime = PerHandlerConnectionLifetime,
        };
#else
        return new HttpClientHandler
        {
            Proxy = endpoint.ToWebProxy(),
            UseProxy = true,
        };
#endif
    }

    /// <summary>
    /// Builds the real, production low-level handler for the no-proxy-available fallback (behaviour
    /// 7) when <see cref="DelegatingHandler.InnerHandler"/> was never assigned — see
    /// <see cref="SendDirectAsync"/>. No proxy is configured on it: this is "behave like an ordinary,
    /// unproxied <see cref="HttpClient"/>", not "force no proxy at all" — whatever system/environment
    /// proxy configuration the OS provides still applies, exactly as it would for a plain
    /// <c>new HttpClient()</c>.
    /// </summary>
    internal static HttpMessageHandler CreateDefaultDirectHandler()
    {
#if NET
        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = PerHandlerConnectionLifetime,
        };
#else
        return new HttpClientHandler();
#endif
    }

    /// <inheritdoc />
    /// <remarks>
    /// Two independent try/catch blocks, deliberately not one: the first attributes a transport
    /// failure (the actual network attempt through the leased proxy) to that proxy; the second
    /// evaluates <c>classifyResponse</c> completely outside that attribution — a bug in caller-supplied
    /// classification code must never be reported as a fault of a proxy that just delivered a response
    /// perfectly correctly. If classification itself throws, nothing is reported for this attempt (not
    /// even a fallback classification — a broken classifier deserves a loud failure so the caller
    /// notices and fixes it, not a silently degraded signal), the response is disposed since it will
    /// never reach the caller, and the classifier's own exception propagates unchanged.
    /// </remarks>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
#if NET
        ArgumentNullException.ThrowIfNull(request);
#else
#pragma warning disable CA1510
        if (request is null) throw new ArgumentNullException(nameof(request));
#pragma warning restore CA1510
#endif

        ProxyEndpoint? proxy = _source.Lease(_tags);
        if (proxy is null)
        {
            return await SendDirectAsync(request, cancellationToken).ConfigureAwait(false);
        }

        HttpMessageInvoker invoker = GetOrCreateInvoker(proxy);
        HttpResponseMessage response;

        // Attributes ONLY a failure of the actual send through this proxy. Catching the general
        // Exception type is deliberate, not a shortcut: ProxyOutcomeClassifier.FromException already
        // has a considered answer (Failure) for whatever it does not specifically recognize, and this
        // handler's whole contract is "observe, never change control flow" — swallowing here instead of
        // rethrowing would silently turn a real transport failure into an apparent success at the caller.
#pragma warning disable CA1031
        try
        {
            response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _source.Report(proxy.Id, ProxyOutcomeClassifier.FromException(ex), ex.Message);
            throw;
        }
#pragma warning restore CA1031

        // Deliberately a SEPARATE try, outside the one above — see this method's own remarks and the
        // type's remarks ("Reporting, not control flow") for why a classifyResponse bug must never be
        // attributed to the proxy that just answered correctly.
#pragma warning disable CA1031 // Same reasoning as above: must catch whatever classifyResponse can
        // throw, not just anticipated types, and it always rethrows — never swallows.
        try
        {
            ProxyOutcome outcome = ProxyOutcomeClassifier.FromResponse(response, _classifyResponse);
            _source.Report(proxy.Id, outcome);
            return response;
        }
        catch (Exception)
        {
            response.Dispose();
            throw;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Behaviour 7: no proxy available. Falls through to the ordinary <see cref="DelegatingHandler"/>
    /// chain when this instance actually has one (the <c>IHttpClientFactory</c>/DI path — see
    /// <c>AddFsProxyRotation</c> — always assigns one) rather than failing the request outright: a
    /// scraper that cannot get a proxy this instant should still get to try, unproxied, not be stopped
    /// cold.
    /// </summary>
    /// <remarks>
    /// The standalone usage this SDK also invites — <c>new HttpClient(new FsProxyRotationHandler(source,
    /// tags))</c>, with no <see cref="DelegatingHandler.InnerHandler"/> ever assigned — must reach the
    /// exact same outcome, not throw. Calling <c>base.SendAsync</c> when <see cref="DelegatingHandler.InnerHandler"/>
    /// is <see langword="null"/> throws <see cref="InvalidOperationException"/> ("The inner handler has
    /// not been assigned") before this handler even gets a chance to send anything — so that case is
    /// routed to a separate, lazily-built default direct <see cref="HttpMessageInvoker"/>
    /// (<see cref="_directInvoker"/>) instead of ever calling <c>base.SendAsync</c> at all.
    /// </remarks>
    private Task<HttpResponseMessage> SendDirectAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (InnerHandler is not null)
        {
            return base.SendAsync(request, cancellationToken);
        }

        return _directInvoker.Value.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Looks up (or, on first sight of this proxy, builds) the cached <see cref="HttpMessageInvoker"/>
    /// for <paramref name="endpoint"/>'s <see cref="ProxyEndpoint.Id"/>, touches its recency stamp, and
    /// — only when this call just added a brand-new entry — checks whether the cache is now over
    /// <see cref="MaxCachedInvokers"/> and evicts the least-recently-used entries if so.
    /// </summary>
    /// <remarks>
    /// The factory passed to <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,Func{TKey,TValue})"/>
    /// can run more than once under a race for the same brand-new key, with every result but the winner
    /// silently discarded — wrapping the actual <see cref="HttpMessageInvoker"/> construction in a
    /// <see cref="Lazy{T}"/> (rather than constructing it directly inside that factory) means only the
    /// single <see cref="Lazy{T}"/> instance that wins the race ever has its value factory invoked, so
    /// at most one <see cref="HttpMessageInvoker"/> (and the connection pool and socket state it owns)
    /// is ever actually built per proxy, regardless of how many raced, discarded <see cref="CacheEntry"/>/
    /// <see cref="Lazy{T}"/> wrapper objects existed only momentarily. The 3-arg
    /// <c>GetOrAdd(key, factory, arg)</c> overload — which would let the factory stay <see langword="static"/>
    /// and avoid the closures below — is not available on netstandard2.0's <c>ConcurrentDictionary</c>.
    /// </remarks>
    private HttpMessageInvoker GetOrCreateInvoker(ProxyEndpoint endpoint)
    {
        Func<ProxyEndpoint, HttpMessageHandler> perProxyHandlerFactory = _perProxyHandlerFactory;
        bool created = false;
        CacheEntry entry = _invokers.GetOrAdd(endpoint.Id, _ =>
        {
            created = true;
            return new CacheEntry(new Lazy<HttpMessageInvoker>(() => new HttpMessageInvoker(perProxyHandlerFactory(endpoint), disposeHandler: true)));
        });

        // A monotonically increasing sequence number, not a wall-clock timestamp: many leases can land
        // within the same clock tick under load, which would make "least recently used" ties resolve
        // arbitrarily. A per-handler Interlocked counter never ties.
        Interlocked.Exchange(ref entry.LastUsedSequence, Interlocked.Increment(ref _sequence));

        if (created)
        {
            EvictLeastRecentlyUsedIfOverCapacity();
        }

        return entry.Invoker.Value;
    }

    /// <summary>
    /// Best-effort cap enforcement: only ever called right after this handler's own
    /// <see cref="GetOrCreateInvoker"/> call just added a brand-new cache entry, so it runs at most
    /// once per distinct proxy this handler ever sees — never on every request. A concurrent burst of
    /// distinct new proxies can cause more than one thread to compute an overlapping victim list at
    /// once; each victim is removed via <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey,out TValue)"/>,
    /// which simply fails harmlessly for whichever thread loses that race — never a double-dispose.
    /// </summary>
    private void EvictLeastRecentlyUsedIfOverCapacity()
    {
        int overflow = _invokers.Count - MaxCachedInvokers;
        if (overflow <= 0)
        {
            return;
        }

        List<Guid> victims = _invokers
            .OrderBy(pair => Interlocked.Read(ref pair.Value.LastUsedSequence))
            .Take(overflow)
            .Select(pair => pair.Key)
            .ToList();

        foreach (Guid key in victims)
        {
            if (_invokers.TryRemove(key, out CacheEntry? removed) && removed.Invoker.IsValueCreated)
            {
                removed.Invoker.Value.Dispose();
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // IsValueCreated guard: disposal must not force-construct (and then immediately throw
            // away) a per-proxy handler that no request ever actually used.
            foreach (Lazy<HttpMessageInvoker> invoker in _invokers.Values.Select(entry => entry.Invoker).Where(invoker => invoker.IsValueCreated))
            {
                invoker.Value.Dispose();
            }

            _invokers.Clear();

            if (_directInvoker.IsValueCreated)
            {
                _directInvoker.Value.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>One cached per-proxy invoker plus the recency bookkeeping <see cref="EvictLeastRecentlyUsedIfOverCapacity"/> needs.</summary>
    private sealed class CacheEntry
    {
        public CacheEntry(Lazy<HttpMessageInvoker> invoker) => Invoker = invoker;

        public Lazy<HttpMessageInvoker> Invoker { get; }

        /// <summary>Set from <see cref="FsProxyRotationHandler._sequence"/> on every touch; read (and raced over) via <see cref="Interlocked"/> only.</summary>
        public long LastUsedSequence;
    }
}
