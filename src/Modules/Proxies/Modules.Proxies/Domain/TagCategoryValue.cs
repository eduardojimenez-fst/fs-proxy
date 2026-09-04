using System.Diagnostics.CodeAnalysis;
using FSH.Framework.Core.Domain;

namespace FSH.Modules.Proxies.Domain;

/// <summary>
/// A plain child entity, not its own aggregate root — mirrors <see cref="ProxyTagAssignment"/>'s
/// shape. Values are never renamed, only added/removed, so the composite key
/// (TagCategoryId, Value) needs no separate surrogate id.
/// </summary>
public sealed class TagCategoryValue : IGlobalEntity
{
    public Guid TagCategoryId { get; private set; }
    public string Value { get; private set; } = default!;

    private TagCategoryValue() { }

    internal static TagCategoryValue Create(Guid tagCategoryId, string value) =>
        new() { TagCategoryId = tagCategoryId, Value = Normalize(value) };

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Tag category values are canonicalized to lowercase by design as a " +
            "stable, human-readable identifier used in the admin UI — not a " +
            "security-sensitive comparison, so CA1308's round-trip concern does not apply.")]
    internal static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant();
    }
}
