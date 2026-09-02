namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record HealthCheckTargetDto(Guid Id, string Name, string TestUrl, int? ExpectedStatusCode, string? ExpectedBodyKeyword, int TimeoutMs);
