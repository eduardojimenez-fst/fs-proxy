using System.Diagnostics.CodeAnalysis;
using FSH.Framework.Core.Domain;

namespace FSH.Modules.Proxies.Domain;

public sealed class Tag : AggregateRoot<Guid>, IGlobalEntity
{
    public string Name { get; private set; } = default!;

    private Tag() { }

    public static Tag Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Tag { Id = Guid.CreateVersion7(), Name = Normalize(name) };
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Tag names are canonicalized to lowercase by design (e.g. \"pais:cl\") as a " +
            "stable, human-readable identifier used in the admin UI and API request bodies — not a " +
            "security-sensitive comparison, so CA1308's round-trip concern does not apply.")]
    public static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim().ToLowerInvariant();
    }
}
