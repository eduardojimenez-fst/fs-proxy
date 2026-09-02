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

    public static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim().ToUpperInvariant();
    }
}
