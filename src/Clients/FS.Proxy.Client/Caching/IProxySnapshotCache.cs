using System.Collections.Generic;

namespace FSH.Proxy.Client.Caching;

/// <summary>
/// A local, best-effort fallback for the last known-good set of proxies for a given tag set. Exists
/// so that a scraper — a batch process that starts, runs, and dies — can still start when the proxy
/// service is unreachable, instead of failing outright the way it never did against the hardcoded
/// proxy list this SDK replaces.
/// </summary>
/// <remarks>
/// Both members are required never to throw. A cache that can fail the process is worse than no
/// cache at all, since the whole point of having one is to widen the process's availability, not
/// narrow it.
/// </remarks>
public interface IProxySnapshotCache
{
    /// <summary>
    /// Returns the most recently written snapshot for <paramref name="key"/>, or <see langword="null"/>
    /// if there is none, it has expired, or it could not be read for any reason (missing file,
    /// corrupt contents, denied permissions, …). Never throws.
    /// </summary>
    IReadOnlyList<ProxyEndpoint>? Read(string key);

    /// <summary>
    /// Best-effort persists <paramref name="endpoints"/> under <paramref name="key"/> for a later
    /// <see cref="Read"/> to find. Failure is silent and total: if the write cannot complete for any
    /// reason, the previous snapshot (if any) is left exactly as it was. Never throws.
    /// </summary>
    void Write(string key, IReadOnlyList<ProxyEndpoint> endpoints);
}
