namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record TagCategoryDto(Guid Id, string Name, IReadOnlyList<string> Values);
