#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices;

/// <summary>
/// Marker type the C# compiler looks for to allow <c>init</c>-accessors and records. Ships in the
/// BCL from net5.0 onward; netstandard2.0 has neither the type nor the compiler's fallback
/// auto-injection of it observed elsewhere for this project, so init-only properties and records
/// fail with CS0518 without this. Internal and empty by design — nothing but its existence and
/// namespace/name match, which the compiler checks structurally, not by any member.
/// </summary>
#pragma warning disable S2094 // Empty by contract: the compiler matches this type structurally by namespace and name only.
internal static class IsExternalInit
{
}
#pragma warning restore S2094
#endif
