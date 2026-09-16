using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FSH.Proxy.Client;

/// <summary>
/// The single surface every adoption level of this SDK uses — a legacy .NET Framework 4.8 scraper on
/// <c>WebRequest</c> touches this type directly (<see cref="ProxySource.Instance"/>); a net10 scraper
/// gets it through DI or the rotation <c>DelegatingHandler</c>.
/// </summary>
public interface IProxySource
{
    /// <summary>
    /// Returns every currently-known proxy for <paramref name="tags"/>, as of the last successful
    /// fetch (warmup or background refresh). Synchronous and performs no I/O — see
    /// <see cref="ProxySource"/>'s remarks for why that matters. For tags that have never been
    /// warmed, or whose last-known snapshot has gone stale, returns an empty list rather than
    /// blocking to fetch one.
    /// </summary>
    IReadOnlyList<ProxyEndpoint> GetProxies(params string[] tags);

    /// <summary>
    /// Returns one proxy for <paramref name="tags"/> in rotation, or <see langword="null"/> if none
    /// is currently available. Synchronous and performs no I/O, exactly like <see cref="GetProxies"/> —
    /// the two differ only in whether the caller wants the whole set or the SDK's own rotation.
    /// </summary>
    ProxyEndpoint? Lease(params string[] tags);

    /// <summary>
    /// Records the outcome of one attempt made through <paramref name="proxyId"/>. Does two things,
    /// deliberately: a non-<see cref="ProxyOutcome.Success"/> outcome quarantines the proxy locally,
    /// immediately, so this process's own next <see cref="Lease"/>/<see cref="GetProxies"/> call stops
    /// offering it — and every outcome (including <see cref="ProxyOutcome.Success"/>, subject to
    /// <c>ProxyClientOptions.SuccessSampling</c>) is enqueued for the service's policy engine to act
    /// on for the whole fleet. Synchronous, non-blocking, and cannot throw.
    /// </summary>
    void Report(Guid proxyId, ProxyOutcome outcome, string? detail = null);

    /// <summary>
    /// Fills the default tag set's pool (<c>ProxyClientOptions.Tags</c>) for the first time and starts
    /// its background refresh timer. Call this once at startup and await it: it is what makes the
    /// very first <see cref="GetProxies"/>/<see cref="Lease"/> call for the default tags deterministic
    /// instead of racing a still-cold pool. A tag set other than the default is populated the first
    /// time <see cref="GetProxies"/>/<see cref="Lease"/> is called for it — that first call still
    /// returns empty/<see langword="null"/> (never blocks to fetch), but it also triggers a background
    /// refresh that fills the pool for the calls after it.
    /// </summary>
    /// <remarks>
    /// Warming is also what activates <c>ProxyClientOptions.SnapshotCache</c> as a fallback for the
    /// tag set being warmed — not merely what makes the first call deterministic.
    /// <see cref="GetProxies"/>/<see cref="Lease"/> never read the cache themselves (they stay
    /// synchronous and I/O-free — see <c>ProxySource</c>'s own remarks for why), and the reactive
    /// refresh a non-default tag set gets from its own first <see cref="GetProxies"/>/<see cref="Lease"/>
    /// call is not a warmup either, so it does not consult the cache. A tag set that is never warmed
    /// gets a <see cref="ProxyClientOptions.SnapshotCache"/> that is faithfully written to (every
    /// successful fetch writes through) and never read — no outage protection at all, despite the
    /// disk file being right there. Equivalent to <c>WarmupAsync(_options.Tags, ct)</c>; see
    /// <see cref="WarmupAsync(string[], CancellationToken)"/> to warm — and activate the cache for —
    /// a tag set other than the default.
    /// </remarks>
    Task WarmupAsync(CancellationToken ct = default);

    /// <summary>
    /// The tag-set-scoped counterpart to <see cref="WarmupAsync(CancellationToken)"/>: fills
    /// <paramref name="tags"/>' own pool for the first time and starts its background refresh timer,
    /// for a consumer that leases against per-call tag sets rather than (or in addition to)
    /// <c>ProxyClientOptions.Tags</c> — the documented, intended usage of <see cref="GetProxies"/>.
    /// Without this overload, such a consumer has no way to warm those pools at all, which also means
    /// no way to activate <c>ProxyClientOptions.SnapshotCache</c> as a fallback for them — see
    /// <see cref="WarmupAsync(CancellationToken)"/>'s remarks.
    /// </summary>
    /// <param name="tags">
    /// The tag set to warm. Normalized and matched to the exact same pool <see cref="GetProxies"/>/
    /// <see cref="Lease"/> read for the identical tag set — order and casing do not matter, mirroring
    /// how those two methods already treat tags. <see langword="null"/> or empty falls back to
    /// <c>ProxyClientOptions.Tags</c>, exactly like <see cref="GetProxies"/>/<see cref="Lease"/> called
    /// with no arguments — so this overload, called with no tags, also warms the default tag set.
    /// </param>
    /// <param name="ct">Propagated to the underlying fetch; does not cancel the write to <c>SnapshotCache</c> on success.</param>
    Task WarmupAsync(string[] tags, CancellationToken ct = default);
}
