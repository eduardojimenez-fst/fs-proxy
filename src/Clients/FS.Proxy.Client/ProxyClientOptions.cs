using System;
using System.Threading;
using FSH.Proxy.Client.Caching;

namespace FSH.Proxy.Client;

/// <summary>
/// Every knob the client exposes. Constructible by hand on purpose: the .NET Framework 4.8 scrapers
/// may have no <c>IConfiguration</c> at all, so binding is a net10 convenience, never a requirement.
/// </summary>
public sealed class ProxyClientOptions
{
    /// <summary>Root of the proxy service, e.g. <c>https://proxy-api-qa.falcontenders.com</c>.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// The scraper's own API key. Bindable from configuration like every other option here — an
    /// <c>"ApiKey"</c> entry in the <c>FsProxy</c> section is supported and supported deliberately.
    /// </summary>
    /// <remarks>
    /// These are internal systems on an internal network, and the alternative — every host needing a
    /// correctly-injected environment variable before it can lease a single proxy — costs more in
    /// misconfigured deployments than it buys. An environment variable still works and still wins
    /// where a host prefers it (<c>FsProxy__ApiKey</c> through the ordinary configuration pipeline, or
    /// whatever name a Level-0 caller reads by hand), so a deployment that already injects one keeps
    /// working unchanged. If you put the key in a committed file, that file's exposure is now the
    /// key's exposure: rotate through the FS Proxy admin rather than treating it as a secret.
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>Tags this client leases against when none are passed explicitly.</summary>
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>
    /// How many proxies to hold locally per tag set. Hard ceiling of 50: the service's
    /// RequestProxiesQueryValidator caps <c>count</c> there, and a larger value is silently
    /// truncated rather than rejected.
    /// </summary>
    public int PoolSize { get; set; } = 50;

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Spread of the refresh interval, so N scrapers starting together do not synchronize.</summary>
    public int RefreshJitterPercent { get; set; } = 20;

    /// <summary>
    /// How long a stale snapshot keeps being served when the service is unreachable. Past this, the
    /// client fails loudly. Without it a 30-second network blip strands every scraper at once.
    /// </summary>
    public TimeSpan StaleCeiling { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a locally-failed proxy is set aside before being offered again.</summary>
    public TimeSpan Quarantine { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan FeedbackFlushInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Events per flush. The service's batch endpoint caps a submission at 200.</summary>
    public int FeedbackBatchSize { get; set; } = 50;

    /// <summary>Bound on the in-memory feedback queue. Overflow drops rather than blocking a scrape.</summary>
    public int FeedbackQueueCapacity { get; set; } = 10_000;

    /// <summary>
    /// Fraction of Success outcomes actually transmitted, 0 to 1. Defaults to 0 because the
    /// service's PolicyEvaluationService counts only non-Success events — successes are rows no
    /// decision reads. Raise it only if you want the server-side series for its own sake.
    /// </summary>
    public double SuccessSampling { get; set; }

    /// <summary>
    /// Local fallback for the last known-good proxy set. Defaults to <see langword="null"/>, i.e. no
    /// local fallback: a service outage is then a hard failure, same as before this cache existed.
    /// Opt in with a <see cref="FileSnapshotCache"/>, or a custom <see cref="IProxySnapshotCache"/>.
    /// </summary>
    /// <remarks>
    /// <b>Read only during warmup.</b> This cache is consulted exclusively from
    /// <c>IProxySource.WarmupAsync</c> (either overload) — never from <c>GetProxies</c>/<c>Lease</c>,
    /// which are deliberately synchronous and perform no I/O, cache reads included. Every successful
    /// fetch — warmup or background refresh — writes through to this cache regardless, so a caller
    /// that configures <see cref="SnapshotCache"/> but never calls <c>WarmupAsync</c> for a given tag
    /// set gets a cache that is faithfully WRITTEN to and NEVER READ for that tag set — no outage
    /// protection at all, no matter how current the on-disk snapshot is. Warm every tag set you
    /// intend to lease against if you want this cache to actually do anything for it.
    /// </remarks>
    public IProxySnapshotCache? SnapshotCache { get; set; }

    /// <summary>
    /// Fires on a DELIBERATE host shutdown — the only case <c>FsProxyRotationHandler</c> can actually
    /// tell apart from an ordinary <c>HttpClient.Timeout</c> firing. Defaults to
    /// <see cref="CancellationToken.None"/> (never fires). See <c>FsProxyRotationHandler</c>'s own
    /// remarks for why this exists: <c>HttpClient</c> links the caller's own token with its
    /// <c>Timeout</c> into ONE token before a <c>DelegatingHandler</c> ever sees it, so a cancelled
    /// <c>SendAsync</c> token is, from inside the handler, indistinguishable from the proxy having
    /// gone silent — UNLESS this token specifically is the one that fired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Level-0/1 caller with its own shutdown signal (or standalone Level-2 usage with no DI
    /// container) sets this by hand directly. Level 2's DI path (<c>AddFsProxyClient</c>) ALSO wires
    /// it, from <c>IHostApplicationLifetime.ApplicationStopping</c> — but only when that service is
    /// actually registered in the container. On a bare <c>ServiceCollection</c> with no generic host
    /// behind it (a console app wiring DI up by hand, a worker outside
    /// <c>Host.CreateDefaultBuilder</c>, a test harness), no
    /// <c>IHostApplicationLifetime</c> is registered at all — in that case, whatever this property was
    /// already set to (by hand, or via the <c>configureOptions</c> overload of
    /// <c>AddFsProxyClient</c>) is left exactly as it was. It is NOT reset to
    /// <see cref="CancellationToken.None"/> in that case: doing so would silently stop recognizing a
    /// shutdown token the caller correctly identified, and every in-flight request that token then
    /// cancelled would report <c>Timeout</c> against an otherwise-healthy proxy — the exact over-blame
    /// direction this property exists to bound.
    /// </para>
    /// <para>
    /// When an <c>IHostApplicationLifetime</c> IS registered, its <c>ApplicationStopping</c> wins
    /// unconditionally over whatever this property already held, including a value set through
    /// <c>configureOptions</c> — the DI path assumes that when a real host lifetime is present, its own
    /// shutdown signal is the authoritative one.
    /// </para>
    /// </remarks>
    public CancellationToken ShutdownToken { get; set; } = CancellationToken.None;

    public void Validate()
    {
        if (BaseAddress is null) throw new InvalidOperationException($"{nameof(BaseAddress)} is required.");
        if (string.IsNullOrWhiteSpace(ApiKey)) throw new InvalidOperationException($"{nameof(ApiKey)} is required.");
        if (PoolSize < 1 || PoolSize > 50) throw new InvalidOperationException($"{nameof(PoolSize)} must be between 1 and 50.");
        if (FeedbackBatchSize < 1 || FeedbackBatchSize > 200) throw new InvalidOperationException($"{nameof(FeedbackBatchSize)} must be between 1 and 200.");
        if (SuccessSampling < 0 || SuccessSampling > 1) throw new InvalidOperationException($"{nameof(SuccessSampling)} must be between 0 and 1.");
        if (RefreshJitterPercent < 0 || RefreshJitterPercent > 100) throw new InvalidOperationException($"{nameof(RefreshJitterPercent)} must be between 0 and 100.");

        // The remaining knobs were previously unchecked: a 0 RefreshInterval yields a ~1ms refresh
        // loop hammering the service, a 0 FeedbackQueueCapacity silently drops every single event
        // Report() is ever called with, and a negative/zero StaleCeiling or FeedbackFlushInterval
        // makes the pool/feedback timers misbehave in ways that are hard to diagnose from the
        // symptom alone. Every TimeSpan/int knob on this type now has a validated range, not only
        // the five that happened to be checked first.
        if (RefreshInterval <= TimeSpan.Zero) throw new InvalidOperationException($"{nameof(RefreshInterval)} must be greater than zero.");
        if (StaleCeiling <= TimeSpan.Zero) throw new InvalidOperationException($"{nameof(StaleCeiling)} must be greater than zero.");
        if (Quarantine < TimeSpan.Zero) throw new InvalidOperationException($"{nameof(Quarantine)} must not be negative.");
        if (FeedbackFlushInterval <= TimeSpan.Zero) throw new InvalidOperationException($"{nameof(FeedbackFlushInterval)} must be greater than zero.");
        if (FeedbackQueueCapacity < 1) throw new InvalidOperationException($"{nameof(FeedbackQueueCapacity)} must be at least 1.");
    }
}
