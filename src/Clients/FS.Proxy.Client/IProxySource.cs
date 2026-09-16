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
    Task WarmupAsync(CancellationToken ct = default);
}
