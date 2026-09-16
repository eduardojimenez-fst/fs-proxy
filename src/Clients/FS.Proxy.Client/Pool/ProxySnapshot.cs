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

        // Defensively copied, ALWAYS — not merely referenced, and not via an "endpoints as
        // ProxyEndpoint[] ?? endpoints.ToArray()" fast path either. IProxyServiceClient and
        // IProxySnapshotCache are both public extension points, so nothing here can assume a
        // caller-supplied list is never mutated or reused after this constructor returns —
        // ProxyServiceClient/FileSnapshotCache happen to allocate a fresh List<T> per call today, but
        // a `ProxyEndpoint[]` is one of the most natural shapes for a custom IProxyServiceClient or
        // IProxySnapshotCache to hand back (e.g. straight out of
        // JsonSerializer.Deserialize<ProxyEndpoint[]>), and an `as ProxyEndpoint[]` fast path would
        // skip the copy for exactly that shape. An aliased array cannot shrink out from under Next()'s
        // captured count the way a List<T> could, but a caller mutating an element in place after
        // handing the array over would silently serve wrong proxy data through the live snapshot —
        // worse than the ArgumentOutOfRangeException the List<T> case risks, because it never throws
        // and never announces itself. Always copying makes "never mutated after construction" (see the
        // doc comment below) an invariant enforced against any implementation, not one that happens to
        // hold for the two shipped today.
        Endpoints = endpoints.ToArray();
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
