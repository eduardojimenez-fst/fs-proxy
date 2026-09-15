using System;
using System.Collections.Generic;
using System.Linq;

namespace FSH.Proxy.Client.Pool;

/// <summary>
/// One immutable, point-in-time view of a pool's proxies. <see cref="ProxyPool"/> swaps this by a
/// single reference assignment on every successful fetch, which is what lets
/// <see cref="ProxyPool.Next"/> read it without ever locking or doing I/O: the object itself never
/// changes after construction, so a concurrent reader always sees either the whole old snapshot or
/// the whole new one — never a torn mix of the two.
/// </summary>
internal sealed class ProxySnapshot
{
    public ProxySnapshot(IReadOnlyList<ProxyEndpoint> endpoints, DateTimeOffset fetchedAt)
    {
#if NET
        ArgumentNullException.ThrowIfNull(endpoints);
#else
#pragma warning disable CA1510
        if (endpoints is null) throw new ArgumentNullException(nameof(endpoints));
#pragma warning restore CA1510
#endif

        // Defensively copied, not merely referenced: IProxyServiceClient and IProxySnapshotCache are
        // both public extension points, so nothing here can assume a caller-supplied list is never
        // mutated or reused after this constructor returns. ProxyServiceClient/FileSnapshotCache
        // happen to allocate a fresh list per call today, so there is no aliasing in the shipped
        // implementations — but Next() captures Endpoints.Count once and indexes by it afterward; a
        // collaborator's list that shrank out from under that captured count would make Next() throw
        // ArgumentOutOfRangeException on the very read path this type exists to keep safe. One
        // allocation per accepted fetch (not per Next() call) makes the "never mutated after
        // construction" doc comment below an enforced invariant instead of a hoped-for one.
        Endpoints = endpoints as ProxyEndpoint[] ?? endpoints.ToArray();
        FetchedAt = fetchedAt;
    }

    /// <summary>The proxies as of <see cref="FetchedAt"/>. Never mutated after construction.</summary>
    public IReadOnlyList<ProxyEndpoint> Endpoints { get; }

    /// <summary>
    /// When this snapshot was fetched, per the pool's injected clock. This — not wall-clock time —
    /// is what <see cref="ProxyPool.IsStale"/> compares against <c>StaleCeiling</c>, which is what
    /// makes staleness testable without <c>Thread.Sleep</c>.
    /// </summary>
    public DateTimeOffset FetchedAt { get; }

    /// <summary>The pool's initial state before any successful fetch: no proxies, "fetched" at construction time.</summary>
    public static ProxySnapshot Empty(DateTimeOffset fetchedAt) => new(Array.Empty<ProxyEndpoint>(), fetchedAt);
}
