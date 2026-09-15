using System.Collections.Generic;

namespace FSH.Proxy.Client.Caching;

/// <summary>
/// The no-op <see cref="IProxySnapshotCache"/>: <see cref="Read"/> always returns <see langword="null"/>
/// and <see cref="Write"/> always does nothing. This is what <see cref="ProxyClientOptions.SnapshotCache"/>
/// defaulting to <see langword="null"/> effectively means for a caller that never checks for null before
/// use — a single shared instance rather than allocating a fresh do-nothing object per client.
/// </summary>
public sealed class NullSnapshotCache : IProxySnapshotCache
{
    /// <summary>The single shared instance. This type carries no state, so there is never a reason for more than one.</summary>
    public static readonly NullSnapshotCache Instance = new();

    private NullSnapshotCache()
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<ProxyEndpoint>? Read(string key) => null;

    /// <inheritdoc />
    public void Write(string key, IReadOnlyList<ProxyEndpoint> endpoints)
    {
        // Intentionally a no-op.
    }
}
