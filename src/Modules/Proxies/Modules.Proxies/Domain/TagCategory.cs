using FSH.Framework.Core.Domain;

namespace FSH.Modules.Proxies.Domain;

/// <summary>
/// A purely advisory catalog of tag "dimensions" (e.g. "pais") and their allowed values (e.g.
/// "cl"), used only to compose "{category}:{value}" strings for the admin tag-assignment UI.
/// Deliberately has no foreign key to <see cref="Tag"/> or <see cref="ProxyTagAssignment"/> — a
/// tag composed from this catalog is indistinguishable from one typed by hand, and deleting a
/// category or value never touches already-assigned tags.
/// </summary>
public sealed class TagCategory : AggregateRoot<Guid>, IGlobalEntity
{
    public string Name { get; private set; } = default!;

    private readonly List<TagCategoryValue> _values = [];
    public IReadOnlyCollection<TagCategoryValue> Values => _values;

    private TagCategory() { }

    public static TagCategory Create(string name) =>
        new() { Id = Guid.CreateVersion7(), Name = Normalize(name) };

    public void Rename(string name) => Name = Normalize(name);

    public void AddValue(string value)
    {
        var normalized = TagCategoryValue.Normalize(value);
        if (_values.Any(v => string.Equals(v.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Value \"{normalized}\" already exists in category \"{Name}\".");
        }
        _values.Add(TagCategoryValue.Create(Id, normalized));
    }

    public void RemoveValue(string value)
    {
        var normalized = TagCategoryValue.Normalize(value);
        _values.RemoveAll(v => string.Equals(v.Value, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }
}
