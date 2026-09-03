namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record TagDto(Guid Id, string Name, Guid? PolicyProfileId, string? PolicyProfileName, Guid? HealthCheckTargetId, string? HealthCheckTargetName);
