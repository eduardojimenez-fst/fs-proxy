namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record ApiClientDto(Guid Id, string Name, bool IsEnabled, DateTime CreatedAtUtc, DateTime? LastUsedAtUtc);
