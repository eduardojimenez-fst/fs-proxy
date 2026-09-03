namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record PolicyProfileDto(Guid Id, string Name, PolicyProfileType Type, int FailureThreshold, int WindowMinutes, int MinDistinctReporters);
