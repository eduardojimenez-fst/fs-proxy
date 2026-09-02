using FSH.Framework.Core.Domain;

namespace FSH.Modules.Proxies.Domain;

public sealed class ApiClient : AggregateRoot<Guid>, IGlobalEntity
{
    public string Name { get; private set; } = default!;
    public string ApiKeyHash { get; private set; } = default!;
    public bool IsEnabled { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastUsedAtUtc { get; private set; }

    private ApiClient() { }

    public static ApiClient Create(string name, string apiKeyHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyHash);
        return new ApiClient
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            ApiKeyHash = apiKeyHash,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    public void RecordUsage() => LastUsedAtUtc = DateTime.UtcNow;
}
