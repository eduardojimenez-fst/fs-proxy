using System;

namespace FSH.Proxy.Client;

/// <summary>
/// Every knob the client exposes. Constructible by hand on purpose: the .NET Framework 4.8 scrapers
/// may have no <c>IConfiguration</c> at all, so binding is a net10 convenience, never a requirement.
/// </summary>
public sealed class ProxyClientOptions
{
    /// <summary>Root of the proxy service, e.g. <c>https://proxy-qa.falconsoft.cl</c>.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>The scraper's own API key. Supply from an environment variable, never from a config file.</summary>
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

    public void Validate()
    {
        if (BaseAddress is null) throw new InvalidOperationException($"{nameof(BaseAddress)} is required.");
        if (string.IsNullOrWhiteSpace(ApiKey)) throw new InvalidOperationException($"{nameof(ApiKey)} is required.");
        if (PoolSize < 1 || PoolSize > 50) throw new InvalidOperationException($"{nameof(PoolSize)} must be between 1 and 50.");
        if (FeedbackBatchSize < 1 || FeedbackBatchSize > 200) throw new InvalidOperationException($"{nameof(FeedbackBatchSize)} must be between 1 and 200.");
        if (SuccessSampling < 0 || SuccessSampling > 1) throw new InvalidOperationException($"{nameof(SuccessSampling)} must be between 0 and 1.");
        if (RefreshJitterPercent < 0 || RefreshJitterPercent > 100) throw new InvalidOperationException($"{nameof(RefreshJitterPercent)} must be between 0 and 100.");
    }
}
