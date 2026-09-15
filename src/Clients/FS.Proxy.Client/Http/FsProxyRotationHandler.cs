using System;
using System.Collections.Concurrent;
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
/// concurrently in flight through the same (pooled, shared) primary handler. Two ways out of that
/// were considered:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Chosen: this handler owns a small cache of <see cref="HttpMessageInvoker"/>, one per leased
/// proxy endpoint (keyed by <see cref="ProxyEndpoint.Id"/>), each wrapping its own
/// <see cref="HttpClientHandler"/> constructed once with that endpoint's <see cref="ProxyEndpoint.ToWebProxy"/>.
/// </b> <see cref="SendAsync"/> leases a proxy, looks up (or builds) that proxy's invoker, and sends
/// through it directly — it deliberately does NOT call <c>base.SendAsync</c> when a proxy was leased,
/// because doing so would still funnel every request through whatever single primary handler the
/// <c>HttpClient</c> was configured with. The trade-off this buys: a primary handler configured on the
/// owning <c>HttpClient</c> (or any <see cref="DelegatingHandler"/> registered to run <i>after</i> this
/// one in the pipeline) is never reached while a proxy is in play, since this handler <i>is</i> the
/// terminal handler for that request. <see cref="DelegatingHandler.InnerHandler"/> is used only for the
/// no-proxy-available fallback (behaviour 7) — see below.
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
/// establishment) for a benefit — one fewer cached handler per proxy — that does not matter at this
/// SDK's scale (a pool tops out at <c>ProxyClientOptions.PoolSize</c>, 50). The cache approach needs
/// nothing from the consumer beyond adding this handler.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>Reporting, not control flow.</b> A response is always returned and an exception is always
/// rethrown unchanged (see behaviour 5) — this handler's only side effect on the happy or unhappy
/// path is the call to <see cref="IProxySource.Report"/>. <see cref="ProxyOutcomeClassifier.FromResponse"/>
/// also means a destination's own 4xx/5xx (503 included) reports <see cref="ProxyOutcome.Success"/>:
/// the proxy did its job, the portal's own problem is not the proxy's fault. The constructor's
/// <c>classifyResponse</c> parameter is the hook for a caller that recognizes a soft block — a 200
/// response whose body is actually a captcha page, which no generic status-code rule can ever detect
/// and which is the single most valuable signal these scrapers have.
/// </para>
/// </remarks>
public sealed class FsProxyRotationHandler : DelegatingHandler
{
    private readonly IProxySource _source;
    private readonly string[] _tags;
    private readonly Func<HttpResponseMessage, ProxyOutcome?>? _classifyResponse;
    private readonly Func<ProxyEndpoint, HttpMessageHandler> _perProxyHandlerFactory;
    private readonly ConcurrentDictionary<Guid, Lazy<HttpMessageInvoker>> _invokers = new();

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
        : this(source, tags, classifyResponse, perProxyHandlerFactory: null)
    {
    }

    /// <summary>
    /// Test seam: substitutes the factory that turns a leased <see cref="ProxyEndpoint"/> into the
    /// low-level <see cref="HttpMessageHandler"/> that actually sends through it. Production code
    /// always goes through the public constructor, which defaults this to <see cref="CreateDefaultPerProxyHandler"/>.
    /// </summary>
    internal FsProxyRotationHandler(
        IProxySource source,
        string[] tags,
        Func<HttpResponseMessage, ProxyOutcome?>? classifyResponse,
        Func<ProxyEndpoint, HttpMessageHandler>? perProxyHandlerFactory)
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
        _tags = tags;
        _classifyResponse = classifyResponse;
        _perProxyHandlerFactory = perProxyHandlerFactory ?? CreateDefaultPerProxyHandler;
    }

    /// <summary>
    /// Builds the real, production low-level handler for one proxy endpoint: an
    /// <see cref="HttpClientHandler"/> whose <see cref="HttpClientHandler.Proxy"/> is
    /// <paramref name="endpoint"/>'s own <see cref="ProxyEndpoint.ToWebProxy"/>. <see cref="HttpClientHandler"/>,
    /// not <c>SocketsHttpHandler</c> (which does not exist on the netstandard2.0 target at all), on
    /// purpose: it is the one primary handler
    /// type available on both this package's targets (netstandard2.0 and net10.0) — see
    /// <see cref="ProxyEndpoint.ToWebProxy"/>'s own remarks for the SOCKS5 caveat that follows from that
    /// choice on the netstandard2.0 target.
    /// </summary>
    internal static HttpClientHandler CreateDefaultPerProxyHandler(ProxyEndpoint endpoint) => new()
    {
        Proxy = endpoint.ToWebProxy(),
        UseProxy = true,
    };

    /// <inheritdoc />
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
            // Behaviour 7: no proxy available. Falls through to the ordinary DelegatingHandler chain
            // (InnerHandler) rather than failing the request outright — a scraper that cannot get a
            // proxy this instant should still get to try, unproxied, not be stopped cold.
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        HttpMessageInvoker invoker = GetOrCreateInvoker(proxy);

        // This handler's entire contract is "observe, never change control flow": every branch below
        // reports exactly once and either returns the real response or rethrows the real exception
        // completely unchanged (see behaviour 5). Catching the general Exception type is therefore
        // deliberate, not a shortcut — ProxyOutcomeClassifier.FromException already has a considered
        // answer (Failure) for whatever it does not specifically recognize, and swallowing anything
        // here instead of rethrowing would silently turn a real transport failure into an apparent
        // success at the caller.
#pragma warning disable CA1031
        try
        {
            HttpResponseMessage response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _source.Report(proxy.Id, ProxyOutcomeClassifier.FromResponse(response, _classifyResponse));
            return response;
        }
        catch (Exception ex)
        {
            _source.Report(proxy.Id, ProxyOutcomeClassifier.FromException(ex), ex.Message);
            throw;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Looks up (or, on first sight of this proxy, builds) the cached <see cref="HttpMessageInvoker"/>
    /// for <paramref name="endpoint"/>'s <see cref="ProxyEndpoint.Id"/>. Wrapped in a <see cref="Lazy{T}"/>
    /// so that a race between two concurrent requests leasing the same brand-new proxy at once
    /// constructs exactly one <see cref="HttpMessageInvoker"/> (and the <see cref="HttpClientHandler"/>,
    /// connection pool and socket state it owns) rather than <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,Func{TKey,TValue})"/>'s
    /// usual caveat of possibly invoking the factory more than once and silently leaking every result
    /// but the one that won.
    /// </summary>
    private HttpMessageInvoker GetOrCreateInvoker(ProxyEndpoint endpoint)
    {
        // The 3-arg GetOrAdd(key, factory, arg) overload — which would let the factory stay static
        // and avoid this closure allocation — is not available on netstandard2.0's ConcurrentDictionary,
        // so this captures `endpoint` and `_perProxyHandlerFactory` instead. Harmless: the closure itself
        // holds no disposable resource, so a race that allocates more than one of these ordinary
        // delegate objects still leaves the Lazy<T> guarantee (see the member's own remarks) doing the
        // real job of ensuring only one HttpMessageInvoker is ever actually constructed.
        Func<ProxyEndpoint, HttpMessageHandler> perProxyHandlerFactory = _perProxyHandlerFactory;
        return _invokers.GetOrAdd(
            endpoint.Id,
            _ => new Lazy<HttpMessageInvoker>(() => new HttpMessageInvoker(perProxyHandlerFactory(endpoint), disposeHandler: true))).Value;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Lazy<HttpMessageInvoker> invoker in _invokers.Values)
            {
                // IsValueCreated guard: disposal must not force-construct (and then immediately throw
                // away) a per-proxy handler that no request ever actually used.
                if (invoker.IsValueCreated)
                {
                    invoker.Value.Dispose();
                }
            }

            _invokers.Clear();
        }

        base.Dispose(disposing);
    }
}
